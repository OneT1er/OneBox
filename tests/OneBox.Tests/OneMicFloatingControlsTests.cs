using System;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using Xunit;

namespace PowerAudioManager.Tests;

public class OneMicFloatingControlsTests
{
    private sealed class UiCapture
    {
        public WeakReference Content { get; }
        public WeakReference Focus { get; }
        public WeakReference Root { get; }
        public UiCapture(WeakReference content, WeakReference focus = null, WeakReference root = null)
        {
            Content = content;
            Focus = focus;
            Root = root;
        }
    }

    [Fact]
    public void FoldingAndHidingReleaseUiTreesAndRestoreWithoutStoppingSampling()
    {
        Exception failure = null;
        var thread = new Thread(() =>
        {
            Application app = null;
            MainWindow window = null;
            Timer sampler = null;
            try
            {
                app = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
                window = new MainWindow();
                var type = typeof(MainWindow);
                AssertOneMicRefreshControls(window, type);

                var contentPanel = Field(type, "_contentPanel");
                var mainBorder = Field(type, "_mainBorder");
                var expanded = Field(type, "_isExpanded");
                var collapsedManually = Field(type, "_collapsedManually");
                var tempTimer = Field(type, "_tempTimer");
                var screenPoll = Field(type, "_screenPoll");
                var uiReclaimTimer = Field(type, "_uiReclaimTimer");
                var scheduleUiReclamation = type.GetMethod("ScheduleUiReclamation", BindingFlags.Instance | BindingFlags.NonPublic);
                Assert.NotNull(scheduleUiReclamation);
                var releaseHiddenUi = type.GetMethod("ReleaseHiddenWindowUi", BindingFlags.Instance | BindingFlags.NonPublic);
                Assert.NotNull(releaseHiddenUi);
                var onLoaded = type.GetMethod("OnLoaded", BindingFlags.Instance | BindingFlags.NonPublic,
                    null, new[] { typeof(object), typeof(RoutedEventArgs) }, null);
                Assert.NotNull(onLoaded);
                window.Loaded -= (RoutedEventHandler)Delegate.CreateDelegate(typeof(RoutedEventHandler), window, onLoaded);
                window.ShowActivated = false;
                window.ShowInTaskbar = false;
                window.Opacity = 0;
                var setExpanded = type.GetMethod("SetExpanded", BindingFlags.Instance | BindingFlags.NonPublic,
                    null, new[] { typeof(bool), typeof(bool) }, null);
                Assert.NotNull(setExpanded);

                var scaleType = type.Assembly.GetType("PowerAudioManager.WindowScaling", throwOnError: true);
                var scaleConstructor = scaleType.GetConstructor(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic,
                    null, new[] { typeof(Window), typeof(Func<Border>) }, null);
                var scaling = scaleConstructor.Invoke(new object[] { window, new Func<Border>(() => (Border)mainBorder.GetValue(window)) });
                Field(type, "_scaling").SetValue(window, scaling);
                scaleType.GetMethod("ApplyScaling").Invoke(scaling, null);
                var appliedBorder = scaleType.GetField("_appliedBorder", BindingFlags.Instance | BindingFlags.NonPublic);

                int samples = 0;
                sampler = new Timer(_ => Interlocked.Increment(ref samples), null, 0, 20);
                tempTimer.SetValue(window, sampler);
                int studioListenersBefore = GetStudioChangedSubscriberCount(type);
                window.Left = SystemParameters.WorkArea.Left + 100;
                window.Top = SystemParameters.WorkArea.Top + 100;
                window.Show();
                PumpDispatcher(window.Dispatcher);
                if (MainWindow.ModuleVisible("OneMic"))
                    Assert.True(GetStudioChangedSubscriberCount(type) > studioListenersBefore,
                        "The visible OneMic controls did not subscribe to StudioController.Changed.");
                double originalLeft = window.Left;

                for (int cycle = 0; cycle < 3; cycle++)
                {
                    bool manualFold = cycle == 1;
                    double originalBottom = window.Top + window.ActualHeight;
                    var foldCapture = FoldAndCaptureContent(window, contentPanel, mainBorder, setExpanded,
                        manualFold, cycle == 0);
                    PumpDispatcher(window.Dispatcher);
                    if (cycle == 0 && foldCapture.Focus != null)
                    {
                        AssertFocusNoLongerTargets(window, foldCapture.Focus);
                        AssertFocusOutsideOldTree(window, foldCapture.Root);
                    }
                    Assert.Null(contentPanel.GetValue(window));
                    Assert.False((bool)expanded.GetValue(window));
                    Assert.Equal(manualFold, collapsedManually.GetValue(window));
                    Assert.InRange(Math.Abs(originalBottom - (window.Top + window.ActualHeight)), 0, 1);
                    Assert.Equal(studioListenersBefore, GetStudioChangedSubscriberCount(type));
                    var reclaimTimer = Assert.IsType<System.Windows.Threading.DispatcherTimer>(uiReclaimTimer.GetValue(window));
                    Assert.True(reclaimTimer.IsEnabled);
                    Assert.True(reclaimTimer.Interval >= TimeSpan.FromSeconds(3));
                    var scheduledInterval = reclaimTimer.Interval;
                    CollectAndAssertReleased(foldCapture.Content);
                    scheduleUiReclamation.Invoke(window, null);
                    Assert.Equal(scheduledInterval, reclaimTimer.Interval);
                    Assert.Single(((StackPanel)((Border)window.Content).Child).Children);

                    setExpanded.Invoke(window, new object[] { true, false });
                    PumpDispatcher(window.Dispatcher);
                    Assert.False(reclaimTimer.IsEnabled);
                    Assert.True(HasFieldValue(contentPanel, window));
                    Assert.True((bool)expanded.GetValue(window));
                    Assert.False((bool)collapsedManually.GetValue(window));
                    Assert.Equal(originalLeft, window.Left);
                    Assert.Same(sampler, tempTimer.GetValue(window));

                    double hiddenLeft = window.Left, hiddenTop = window.Top;
                    var hiddenCapture = HideAndCaptureContent(window, contentPanel, releaseHiddenUi, cycle == 0);
                    PumpDispatcher(window.Dispatcher);
                    if (hiddenCapture.Focus != null)
                        AssertFocusNoLongerTargets(window, hiddenCapture.Focus);
                    Assert.True(reclaimTimer.IsEnabled);
                    Assert.Null(window.Content);
                    Assert.Null(contentPanel.GetValue(window));
                    Assert.Null(mainBorder.GetValue(window));
                    Assert.Null(appliedBorder.GetValue(scaling));
                    Assert.False(((System.Windows.Threading.DispatcherTimer)screenPoll.GetValue(window)).IsEnabled);
                    Assert.Equal(studioListenersBefore, GetStudioChangedSubscriberCount(type));
                    CollectAndAssertReleased(hiddenCapture.Content);

                    window.Show();
                    PumpDispatcher(window.Dispatcher);
                    Assert.False(reclaimTimer.IsEnabled);
                    Assert.True(WindowHasContent(window));
                    Assert.True(HasFieldValue(contentPanel, window));
                    Assert.True(((System.Windows.Threading.DispatcherTimer)screenPoll.GetValue(window)).IsEnabled);
                    Assert.Same(sampler, tempTimer.GetValue(window));
                    Assert.Equal(hiddenLeft, window.Left);
                    Assert.Equal(hiddenTop, window.Top);
                    Assert.True(SpinWait.SpinUntil(() => Volatile.Read(ref samples) > 0, TimeSpan.FromSeconds(1)),
                        "The background sampler timer stopped during UI lifecycle changes.");
                }
            }
            catch (Exception ex)
            {
                failure = ex is TargetInvocationException { InnerException: not null } invocation
                    ? invocation.InnerException : ex;
            }
            finally
            {
                sampler?.Dispose();
                if (window != null)
                {
                    try { Field(typeof(MainWindow), "_isExiting").SetValue(window, true); } catch { }
                    foreach (string name in new[] { "_refreshTimer", "_screenPoll", "_autoCollapseTimer" })
                        try { (Field(typeof(MainWindow), name).GetValue(window) as System.Windows.Threading.DispatcherTimer)?.Stop(); } catch { }
                    try { typeof(MainWindow).GetMethod("CancelUiReclamation", BindingFlags.Instance | BindingFlags.NonPublic)?.Invoke(window, null); } catch { }
                    try { window.Close(); } catch { }
                }
                app?.Shutdown();
            }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();
        Assert.Null(failure);
    }

    private static FieldInfo Field(Type type, string name) =>
        type.GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)
        ?? throw new InvalidOperationException("MainWindow field not found: " + name);

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void AssertOneMicRefreshControls(MainWindow window, Type type)
    {
        var audioSection = Assert.IsType<StackPanel>(Field(type, "_audioSection").GetValue(window));
        var devices = Assert.IsType<StackPanel>(Field(type, "_audioDeviceSection").GetValue(window));
        var oneMic = Assert.IsType<StackPanel>(Field(type, "_oneMicSection").GetValue(window));
        var content = Assert.IsType<StackPanel>(Field(type, "_contentPanel").GetValue(window));
        type.GetMethod("RenderDevices", BindingFlags.Instance | BindingFlags.NonPublic)
            .Invoke(window, new object[] { null });
        Assert.DoesNotContain(oneMic, audioSection.Children.Cast<UIElement>());
        Assert.Contains(oneMic, content.Children.Cast<UIElement>());
        Assert.Contains(content.Children.OfType<DockPanel>(), panel =>
            panel.Children.OfType<TextBlock>().Any(label => label.Text == "OneMic"));
        var controls = oneMic.Children.OfType<Grid>().Single();
        var buttons = controls.Children.OfType<Button>().ToArray();
        Assert.Equal(8, buttons.Length);
        Assert.All(buttons, button => Assert.True(button.Width >= 40 && button.Height >= 40));
        Assert.Equal(4, buttons.Count(button => Grid.GetRow(button) == 0));
        Assert.Equal(4, buttons.Count(button => Grid.GetRow(button) == 1));
        Assert.Contains(devices.Children.OfType<TextBlock>(), x => x.Text == "未找到音频输出");
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static bool HasFieldValue(FieldInfo field, object target) => field.GetValue(target) != null;

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static bool WindowHasContent(Window window) => window.Content != null;

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static UiCapture FoldAndCaptureContent(MainWindow window, FieldInfo contentPanel, FieldInfo mainBorder,
        MethodInfo setExpanded, bool manualFold, bool captureFocus)
    {
        var target = contentPanel.GetValue(window);
        var focus = captureFocus ? FocusFirstFocusableChild(window, (DependencyObject)target) : null;
        var capture = new UiCapture(new WeakReference(target), focus, new WeakReference(mainBorder.GetValue(window)));
        setExpanded.Invoke(window, new object[] { false, manualFold });
        return capture;
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static UiCapture HideAndCaptureContent(MainWindow window, FieldInfo contentPanel,
        MethodInfo releaseHiddenUi, bool captureFocus)
    {
        var target = contentPanel.GetValue(window);
        var focus = captureFocus ? FocusFirstFocusableChild(window, (DependencyObject)target) : null;
        var capture = new UiCapture(new WeakReference(target), focus);
        releaseHiddenUi.Invoke(window, null);
        window.UpdateLayout();
        window.Hide();
        return capture;
    }

    private static void PumpDispatcher(System.Windows.Threading.Dispatcher dispatcher)
    {
        var frame = new System.Windows.Threading.DispatcherFrame();
        var settleTimer = new System.Windows.Threading.DispatcherTimer(
            TimeSpan.FromMilliseconds(50), System.Windows.Threading.DispatcherPriority.ApplicationIdle,
            (sender, args) =>
            {
                ((System.Windows.Threading.DispatcherTimer)sender).Stop();
                frame.Continue = false;
            }, dispatcher);
        settleTimer.Start();
        System.Windows.Threading.Dispatcher.PushFrame(frame);
    }

    private static void CollectAndAssertReleased(WeakReference target)
    {
        PumpDispatcher(System.Windows.Threading.Dispatcher.CurrentDispatcher);
        GC.Collect(GC.MaxGeneration, GCCollectionMode.Forced, blocking: true, compacting: true);
        GC.WaitForPendingFinalizers();
        GC.Collect(GC.MaxGeneration, GCCollectionMode.Forced, blocking: true, compacting: true);
        string ancestry = target.Target is DependencyObject detached
            ? DescribeParents(detached)
            : "no DependencyObject target";
        Assert.False(target.IsAlive, "The detached WPF UI tree remained rooted after a full collection. " + ancestry);
    }

    private static string DescribeParents(DependencyObject node)
    {
        var parts = new System.Collections.Generic.List<string>();
        for (int depth = 0; node != null && depth < 12; depth++)
        {
            parts.Add(node.GetType().Name);
            node = System.Windows.Media.VisualTreeHelper.GetParent(node)
                ?? System.Windows.LogicalTreeHelper.GetParent(node);
        }
        return "Parent chain: " + string.Join(" -> ", parts);
    }

    private static int GetStudioChangedSubscriberCount(Type mainWindowType)
    {
        var studioType = mainWindowType.Assembly.GetType("PowerAudioManager.AudioStudio.StudioController", throwOnError: true);
        var instance = studioType.GetProperty("Instance", BindingFlags.Static | BindingFlags.Public).GetValue(null);
        var changed = studioType.GetField("Changed", BindingFlags.Instance | BindingFlags.NonPublic).GetValue(instance) as Delegate;
        return changed?.GetInvocationList().Length ?? 0;
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static WeakReference FocusFirstFocusableChild(Window window, DependencyObject root)
    {
        var target = FirstFocusableDescendant(root);
        if (target == null) return null;
        FocusManager.SetFocusedElement(window, target);
        Assert.Same(target, FocusManager.GetFocusedElement(window));
        Assert.Same(target, Keyboard.FocusedElement);
        return new WeakReference(target);
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void AssertFocusNoLongerTargets(Window window, WeakReference previousFocus)
    {
        var oldFocus = previousFocus.Target;
        if (oldFocus == null) return;
        Assert.NotSame(oldFocus, FocusManager.GetFocusedElement(window));
        Assert.NotSame(oldFocus, Keyboard.FocusedElement);
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void AssertFocusOutsideOldTree(Window window, WeakReference oldRootReference)
    {
        var oldRoot = oldRootReference?.Target as DependencyObject;
        if (oldRoot == null) return;
        foreach (var focus in new[] { FocusManager.GetFocusedElement(window), Keyboard.FocusedElement })
        {
            if (focus is not DependencyObject node) continue;
            Assert.False(IsWithinTree(node, oldRoot), "Focus still belongs to the old UI tree: " + DescribeParents(node));
        }
    }

    private static bool IsWithinTree(DependencyObject node, DependencyObject root)
    {
        for (var current = node; current != null;)
        {
            if (ReferenceEquals(current, root)) return true;
            DependencyObject parent = null;
            if (current is System.Windows.Media.Visual || current is System.Windows.Media.Media3D.Visual3D)
                parent = System.Windows.Media.VisualTreeHelper.GetParent(current);
            else if (current is FrameworkContentElement contentElement)
                parent = contentElement.Parent;
            else if (current is FrameworkElement frameworkElement)
                parent = frameworkElement.Parent;
            current = parent ?? LogicalTreeHelper.GetParent(current);
        }
        return false;
    }

    private static UIElement FirstFocusableDescendant(DependencyObject root)
    {
        int count = System.Windows.Media.VisualTreeHelper.GetChildrenCount(root);
        for (int i = 0; i < count; i++)
        {
            var child = System.Windows.Media.VisualTreeHelper.GetChild(root, i);
            if (child is UIElement element && element.Focusable) return element;
            var found = FirstFocusableDescendant(child);
            if (found != null) return found;
        }
        return null;
    }

}
