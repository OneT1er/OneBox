using System;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using Xunit;

namespace PowerAudioManager.Tests;

public class OneMicFloatingControlsTests
{
    [Fact]
    public void RefreshingAudioDevicesPreservesOneMicControls()
    {
        Exception failure = null;
        var thread = new Thread(() =>
        {
            Application app = null;
            MainWindow window = null;
            try
            {
                app = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
                window = new MainWindow();
                var sectionField = typeof(MainWindow).GetField("_audioSection", BindingFlags.Instance | BindingFlags.NonPublic);
                var devicesField = typeof(MainWindow).GetField("_audioDeviceSection", BindingFlags.Instance | BindingFlags.NonPublic);
                var oneMicField = typeof(MainWindow).GetField("_oneMicSection", BindingFlags.Instance | BindingFlags.NonPublic);
                var contentField = typeof(MainWindow).GetField("_contentPanel", BindingFlags.Instance | BindingFlags.NonPublic);
                var render = typeof(MainWindow).GetMethod("RenderDevices", BindingFlags.Instance | BindingFlags.NonPublic);
                var section = Assert.IsType<StackPanel>(sectionField.GetValue(window));
                var devices = Assert.IsType<StackPanel>(devicesField.GetValue(window));
                var oneMic = Assert.IsType<StackPanel>(oneMicField.GetValue(window));
                var content = Assert.IsType<StackPanel>(contentField.GetValue(window));

                render.Invoke(window, new object[] { null });

                Assert.DoesNotContain(oneMic, section.Children.Cast<UIElement>());
                Assert.Contains(oneMic, content.Children.Cast<UIElement>());
                Assert.Contains(content.Children.OfType<DockPanel>(), panel =>
                    panel.Children.OfType<TextBlock>().Any(label => label.Text == "OneMic"));
                Assert.Equal(8, oneMic.Children.OfType<WrapPanel>().Single().Children.OfType<Button>().Count());
                Assert.Contains(devices.Children.OfType<TextBlock>(), x => x.Text == "未找到音频输出");
            }
            catch (Exception ex)
            {
                failure = ex is TargetInvocationException { InnerException: not null } invocation
                    ? invocation.InnerException : ex;
            }
            finally
            {
                window?.Close();
                app?.Shutdown();
            }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();
        Assert.Null(failure);
    }
}
