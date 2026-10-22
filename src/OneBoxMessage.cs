using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;

namespace PowerAudioManager
{
    // Unified confirm / error / info dialog in the OneBox shell, replacing
    // MessageBox.Show so prompts share the app's dark surface.  Esc cancels,
    // Enter confirms; a null owner centers the dialog on screen and keeps it
    // topmost so it is not lost behind the floating window.
    internal static class OneBoxMessage
    {
        internal static bool Confirm(Window owner, string title, string message, string confirmText = "确定", bool danger = false)
        {
            return Show(owner, title, message, confirmText, "取消", danger ? ButtonVariant.Danger : ButtonVariant.Primary,
                danger ? UiKit.WarnColor : (Color?)null);
        }

        internal static void Error(Window owner, string title, string message)
        {
            Show(owner, title, message, "知道了", null, ButtonVariant.Primary, UiKit.DangerColor);
        }

        internal static void Info(Window owner, string title, string message)
        {
            Show(owner, title, message, "知道了", null, ButtonVariant.Primary, null);
        }

        static bool Show(Window owner, string title, string message, string confirmText, string cancelText,
            ButtonVariant confirmVariant, Color? mark)
        {
            if (owner != null && !owner.IsLoaded) owner = null;
            bool confirmed = false;

            var body = new StackPanel { Margin = new Thickness(16, 2, 16, 16) };
            var headline = new DockPanel { LastChildFill = true };
            if (mark.HasValue)
            {
                var badge = new Border
                {
                    Width = 20, Height = 20,
                    CornerRadius = new CornerRadius(10),
                    Background = UiKit.FrozenBrush(mark.Value == UiKit.DangerColor ? UiKit.DangerSurface : UiKit.WarnSurface),
                    Margin = new Thickness(0, 0, 10, 0),
                    VerticalAlignment = VerticalAlignment.Top,
                    Child = new TextBlock
                    {
                        Text = "!",
                        FontSize = 12,
                        FontWeight = FontWeights.Bold,
                        Foreground = UiKit.FrozenBrush(mark.Value),
                        HorizontalAlignment = HorizontalAlignment.Center,
                        VerticalAlignment = VerticalAlignment.Center
                    }
                };
                DockPanel.SetDock(badge, Dock.Left);
                headline.Children.Add(badge);
            }
            var text = new TextBlock
            {
                Text = message ?? "",
                FontSize = 12,
                LineHeight = 18,
                Foreground = UiKit.FrozenBrush(UiKit.TextSecondary),
                TextWrapping = TextWrapping.Wrap
            };
            headline.Children.Add(text);
            body.Children.Add(headline);

            var buttons = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                HorizontalAlignment = HorizontalAlignment.Right,
                Margin = new Thickness(0, 18, 0, 0)
            };
            Button cancel = null;
            if (cancelText != null)
            {
                cancel = UiKit.MakeButton(cancelText, ButtonVariant.Secondary);
                cancel.MinWidth = 72;
                buttons.Children.Add(cancel);
            }
            var ok = UiKit.MakeButton(confirmText ?? "确定", confirmVariant);
            ok.MinWidth = 72;
            ok.Margin = new Thickness(8, 0, 0, 0);
            buttons.Children.Add(ok);
            body.Children.Add(buttons);

            var dlg = OneBoxWindow.Create(owner, string.IsNullOrEmpty(title) ? "OneBox" : title, 340, 180, body, false);
            dlg.SizeToContent = SizeToContent.Height;
            dlg.ShowInTaskbar = owner == null;
            if (owner == null) dlg.Topmost = true;

            ok.Click += (_, _) => { confirmed = true; dlg.Close(); };
            if (cancel != null) cancel.Click += (_, _) => { confirmed = false; dlg.Close(); };
            dlg.PreviewKeyDown += (_, e) =>
            {
                if (e.Key == Key.Escape) { confirmed = false; e.Handled = true; dlg.Close(); }
                else if (e.Key == Key.Enter) { confirmed = true; e.Handled = true; dlg.Close(); }
            };
            dlg.Loaded += (_, _) => ok.Focus();
            try { dlg.ShowDialog(); }
            catch (System.InvalidOperationException ex)
            {
                AppLog.Log("OneBoxMessage", ex);
                return false;
            }
            // Info / Error have no cancel path; report "acknowledged" as true.
            return cancel == null || confirmed;
        }
    }
}
