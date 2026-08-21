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
            string notes = candidate.ReleaseNotes;
            if (notes.Length > 2000) notes = notes.Substring(0, 2000) + "…";
            return await owner.Dispatcher.InvokeAsync(() =>
                MessageBox.Show(owner,
                    $"OneBox {candidate.Version} 已发布。\n当前版本：{CurrentVersion}\n\n{notes}\n\n是否立即下载、校验并安装？",
                    "发现新版本", MessageBoxButton.YesNo, MessageBoxImage.Information) == MessageBoxResult.Yes);
        }

        private static async Task<(Window Dialog, TextBlock Status)> ShowProgressAsync(Window owner,
            CancellationTokenSource cancellation)
        {
            if (owner == null || owner.Dispatcher.HasShutdownStarted)
                throw new OperationCanceledException("主窗口已关闭。", cancellation.Token);
            return await owner.Dispatcher.InvokeAsync(() =>
            {
                var status = new TextBlock
                {
                    Text = "正在下载并校验更新包… 0%",
                    Foreground = Brushes.White,
                    FontSize = 12,
                    Margin = new Thickness(20),
                };
                var dialog = new Window
                {
                    Title = "OneBox 更新",
                    Width = 360,
                    Height = 120,
                    Owner = owner,
                    WindowStartupLocation = WindowStartupLocation.CenterOwner,
                    ResizeMode = ResizeMode.NoResize,
                    Background = new SolidColorBrush(Color.FromRgb(28, 26, 40)),
                    Content = status,
                };
                dialog.Closed += (_, _) => cancellation.Cancel();
                dialog.Show();
                return (dialog, status);
            });
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
