using System;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace PowerAudioManager
{
    /// <summary>
    /// Velopack update entry point. The workflow is task-based end-to-end so
    /// callers never report success before check/download/apply coordination finishes.
    /// </summary>
    public static class UpdateChecker
    {
        public const string Owner = "OneT1er";
        public const string Repo = "OneBox";
        public static string CurrentVersion => ApplicationVersion.Value;

        public static async Task<UpdateOperationResult> CheckAsync(Window owner, bool manual,
            CancellationToken cancellationToken = default)
        {
            var workflow = new UpdateWorkflow(new VelopackUpdateClient(), new UpdateServiceCoordinator());
            UpdateOperationResult check = await workflow.CheckAsync(cancellationToken);
            AppLog.Log("UpdateChecker",
                $"manual={manual} success={check.Success} available={check.UpdateAvailable} code={check.ErrorCode}");
            if (!check.Success || !check.UpdateAvailable) return check;

            bool accepted = await ConfirmUpdateAsync(owner, check.Candidate, cancellationToken);
            if (!accepted)
                return UpdateOperationResult.Failed(UpdateErrorCode.Cancelled, "已取消本次更新。");

            using var linkedCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            (Window dialog, TextBlock status) = await ShowProgressAsync(owner, linkedCancellation);
            try
            {
                var progress = new Progress<int>(value =>
                {
                    if (!dialog.Dispatcher.HasShutdownStarted)
                        status.Text = $"正在下载并校验更新包… {Math.Clamp(value, 0, 100)}%";
                });
                UpdateOperationResult applied = await workflow.DownloadAndApplyAsync(
                    check.Candidate, progress, linkedCancellation.Token);
                AppLog.Log("UpdateChecker",
                    $"apply success={applied.Success} applied={applied.Applied} code={applied.ErrorCode}");
                return applied;
            }
            finally
            {
                await CloseProgressAsync(dialog);
            }
        }

        private static async Task<bool> ConfirmUpdateAsync(Window owner, UpdateCandidate candidate,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (owner == null || owner.Dispatcher.HasShutdownStarted) return false;
            string notes = candidate.ReleaseNotes ?? "";
            if (notes.Length > 2000) notes = notes.Substring(0, 2000) + "…";
            return await owner.Dispatcher.InvokeAsync(() => ShowConfirmDialog(owner, candidate.Version?.ToString(), notes));
        }

        // 「发现新版本」：版本行 + 可滚动更新说明 + 稍后 / 立即更新。
        static bool ShowConfirmDialog(Window owner, string version, string notes)
        {
            bool accepted = false;
            var body = new DockPanel { Margin = new Thickness(16, 0, 16, 16), LastChildFill = true };

            var head = new StackPanel { Margin = new Thickness(0, 0, 0, 10) };
            head.Children.Add(UiKit.Text($"OneBox {version} 已发布", 15, UiKit.TextPrimary, FontWeights.SemiBold));
            var current = UiKit.Text($"当前版本 {CurrentVersion} · 下载后自动校验并安装", 11, UiKit.TextSecondary);
            current.Margin = new Thickness(0, 3, 0, 0);
            head.Children.Add(current);
            DockPanel.SetDock(head, Dock.Top);
            body.Children.Add(head);

            var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 14, 0, 0) };
            var later = UiKit.MakeButton("稍后", ButtonVariant.Secondary);
            later.MinWidth = 72;
            var now = UiKit.MakeButton("立即更新", ButtonVariant.Primary);
            now.MinWidth = 88;
            now.Margin = new Thickness(8, 0, 0, 0);
            buttons.Children.Add(later); buttons.Children.Add(now);
            DockPanel.SetDock(buttons, Dock.Bottom);
            body.Children.Add(buttons);

            var notesBox = new TextBlock
            {
                Text = string.IsNullOrWhiteSpace(notes) ? "（本次更新没有附带说明）" : notes.Trim(),
                FontSize = 12, LineHeight = 18, TextWrapping = TextWrapping.Wrap,
                Foreground = UiKit.FrozenBrush(UiKit.TextSecondary)
            };
            var scroll = new ScrollViewer { VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Content = notesBox, MaxHeight = 260 };
            scroll.Resources[typeof(System.Windows.Controls.Primitives.ScrollBar)] = ThemeTokens.CreateDarkScrollBarStyle();
            body.Children.Add(UiKit.Card(scroll, new Thickness(12, 10, 12, 10)));

            var dlg = OneBoxWindow.Create(owner, "发现新版本", 400, 360, body, false, IconKey.Brand);
            dlg.SizeToContent = SizeToContent.Height;
            later.Click += (_, _) => { accepted = false; dlg.Close(); };
            now.Click += (_, _) => { accepted = true; dlg.Close(); };
            dlg.PreviewKeyDown += (_, e) => { if (e.Key == System.Windows.Input.Key.Escape) { e.Handled = true; dlg.Close(); } };
            dlg.ShowDialog();
            return accepted;
        }

        private static async Task<(Window Dialog, TextBlock Status)> ShowProgressAsync(Window owner,
            CancellationTokenSource cancellation)
        {
            if (owner == null || owner.Dispatcher.HasShutdownStarted)
                throw new OperationCanceledException("主窗口已关闭。", cancellation.Token);
            return await owner.Dispatcher.InvokeAsync(() =>
            {
                var status = UiKit.Text("正在下载并校验更新包… 0%", 12, UiKit.TextPrimary);
                var bar = new ProgressBar { Height = 4, Minimum = 0, Maximum = 100, Value = 0, Margin = new Thickness(0, 10, 0, 0) };
                bar.Template = ProgressTemplate();
                // Progress<int> writes "…NN%" into status; mirror it on the bar.
                var descriptor = System.ComponentModel.DependencyPropertyDescriptor.FromProperty(TextBlock.TextProperty, typeof(TextBlock));
                descriptor?.AddValueChanged(status, (_, _) =>
                {
                    var text = status.Text ?? "";
                    int pct = text.LastIndexOf('%');
                    int sp = pct > 0 ? text.LastIndexOf(' ', pct) : -1;
                    if (sp >= 0 && int.TryParse(text.Substring(sp + 1, pct - sp - 1), out int value)) bar.Value = value;
                });
                var hint = UiKit.Text("安装时 OneBox 会自动重启", 11, UiKit.TextSecondary);
                hint.Margin = new Thickness(0, 8, 0, 0);
                var body = new StackPanel { Margin = new Thickness(16, 0, 16, 18) };
                body.Children.Add(status);
                body.Children.Add(bar);
                body.Children.Add(hint);
                var dialog = OneBoxWindow.Create(owner, "OneBox 更新", 360, 150, body, false, IconKey.Brand);
                dialog.SizeToContent = SizeToContent.Height;
                dialog.Closed += (_, _) => cancellation.Cancel();
                dialog.Show();
                return (dialog, status);
            });
        }

        static ControlTemplate ProgressTemplate()
        {
            var template = new ControlTemplate(typeof(ProgressBar));
            var track = new FrameworkElementFactory(typeof(Border));
            track.Name = "PART_Track";
            track.SetValue(Border.CornerRadiusProperty, new CornerRadius(2));
            track.SetValue(Border.BackgroundProperty, UiKit.FrozenBrush(UiKit.RaisedColor));
            var grid = new FrameworkElementFactory(typeof(Grid));
            grid.AppendChild(track);
            var indicator = new FrameworkElementFactory(typeof(Border));
            indicator.Name = "PART_Indicator";
            indicator.SetValue(Border.CornerRadiusProperty, new CornerRadius(2));
            indicator.SetValue(Border.BackgroundProperty, UiKit.FrozenBrush(UiKit.AccentColor));
            indicator.SetValue(FrameworkElement.HorizontalAlignmentProperty, HorizontalAlignment.Left);
            grid.AppendChild(indicator);
            template.VisualTree = grid;
            return template;
        }

        private static async Task CloseProgressAsync(Window dialog)
        {
            if (dialog == null || dialog.Dispatcher.HasShutdownStarted) return;
            await dialog.Dispatcher.InvokeAsync(() =>
            {
                if (dialog.IsVisible) dialog.Close();
            });
        }
    }

    public static class UpdateCommandBridge
    {
        public static async Task<UpdateOperationResult> ExecuteAsync(
            Func<CancellationToken, Task<UpdateOperationResult>> operation,
            CancellationToken cancellationToken)
        {
            if (operation == null) throw new ArgumentNullException(nameof(operation));
            return await operation(cancellationToken);
        }
    }
}
