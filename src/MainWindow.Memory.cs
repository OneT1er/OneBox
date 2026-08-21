using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using System.IO;
using System.IO.Pipes;
using System.Security.Principal;
using System.Threading;
using OneBox.Contracts;
using PowerAudioManager.Commands;

namespace PowerAudioManager
{
    // 内存清理：状态展示、手动/自动清理（服务管道或管理员直清）、自动清理计时。
    public partial class MainWindow : Window
    {
        void UpdateMemoryUI()
        {
            if (_memStatusLabel == null) return;
            try
            {
                var s = MemoryCleaner.GetStatus();
                if (s == null) return;
                double total = s.TotalBytes / 1024.0 / 1024.0 / 1024.0;
                double avail = s.AvailableBytes / 1024.0 / 1024.0 / 1024.0;
                double used = total - avail;
                double cachedGb = s.CachedBytes / 1024.0 / 1024.0 / 1024.0;
                _memStatusLabel.Text = string.Format("已用 {0:0.0} GB / {1:0.0} GB ({2}%) · 已缓存 {3:0.0} GB", used, total, s.MemoryLoadPercent, cachedGb);
            }
            catch { }
        }

        internal void CleanMemory()
        {
            _ = ExecuteCommandAsync(AppCommandId.MemoryClean, CommandSource.System,
                new MemoryCleanPayload(MemoryCleaner.GetSavedFlags()));
        }

        internal void CleanMemory(MemoryCleaner.CleanFlags flags)
        {
            _ = ExecuteCommandAsync(AppCommandId.MemoryClean, CommandSource.System,
                new MemoryCleanPayload(flags));
        }

        internal async System.Threading.Tasks.Task<CommandResult> CleanMemoryCoreAsync(
            MemoryCleaner.CleanFlags flags, CancellationToken cancellationToken)
        {
            if (!MemoryCleaner.HasSelectedAreas(flags))
            {
                if (_memStatusLabel != null) _memStatusLabel.Text = "未选择清理项目";
                AppLog.Log("MemoryClean", "skipped: no area selected");
                return CommandResult.Fail(CommandErrorCode.Rejected, "未选择内存清理项目。");
            }
            if (_memStatusLabel != null) _memStatusLabel.Text = "正在清理...";
            cancellationToken.ThrowIfCancellationRequested();

            // 非管理员：命令服务（OneBoxSvc）执行清理，无 UAC。
            if (!AdminUtils.IsAdmin())
            {
                try
                {
                    ulong freedBytes = await System.Threading.Tasks.Task.Run(() =>
                    {
                        using (var identity = WindowsIdentity.GetCurrent(TokenAccessLevels.Query))
                        using (var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken))
                        using (var client = new NamedPipeClientStream(".", PipeNames.ForCommand(identity.User?.Value), PipeDirection.InOut,
                            PipeOptions.Asynchronous, TokenImpersonationLevel.Impersonation))
                        {
                            timeout.CancelAfter(TimeSpan.FromSeconds(15));
                            client.ConnectAsync((int)IpcProtocol.ConnectTimeout.TotalMilliseconds, timeout.Token).GetAwaiter().GetResult();
                            PipeServerIdentityVerifier.EnsureLocalSystemServer(client);
                            var request = IpcRequest.Create(IpcCommand.CleanMemory, new CleanMemoryPayload { Flags = (int)flags });
                            IpcFraming.WriteAsync(client, request, timeout.Token).GetAwaiter().GetResult();
                            IpcResponse response = IpcFraming.ReadAsync<IpcResponse>(client, timeout.Token).GetAwaiter().GetResult();
                            if (response.Version != IpcProtocol.Version || response.RequestId != request.RequestId || response.Command != IpcCommand.CleanMemory)
                                throw new IpcProtocolException(IpcErrorCode.InvalidMessage, "内存清理响应协议无效");
                            if (!response.Success)
                                throw new IpcProtocolException(response.ErrorCode, response.ErrorMessage ?? "服务拒绝内存清理请求");
                            CleanMemoryResult result = response.ReadResult<CleanMemoryResult>();
                            return result?.FreedBytes ?? 0;
                        }
                    }, cancellationToken);
                    if (_memStatusLabel != null) _memStatusLabel.Text = string.Format(
                        "已释放 {0:0} MB（服务清理）", freedBytes / 1024.0 / 1024.0);
                    AppLog.Log("MemoryClean", "service freed=" + (int)(freedBytes / 1024 / 1024) + "MB");
                    _ = Dispatcher.BeginInvoke(new Action(UpdateMemoryUI), DispatcherPriority.ApplicationIdle);
                    return CommandResult.Ok(freedBytes);
                }
                catch (OperationCanceledException) { throw; }
                catch (Exception ex)
                {
                    AppLog.Log("MemoryClean", "service pipe rejected/disconnected: " + ex.Message);
                    if (_memStatusLabel != null) _memStatusLabel.Text = "清理失败: " + ex.Message;
                    return CommandResult.Fail(CommandErrorCode.Failed, "内存清理失败：" + ex.Message);
                }
            }

            // 管理员：直接清理。
            try
            {
                var result = await System.Threading.Tasks.Task.Run(() => MemoryCleaner.CleanAll(flags),
                    cancellationToken);
                double freedMb = result.FreedBytes / 1024.0 / 1024.0;
                if (_memStatusLabel != null) _memStatusLabel.Text = string.Format("已释放 {0:0} MB", freedMb);
                AppLog.Log("MemoryClean", "freed=" + (int)freedMb + "MB flags=" + flags);
                _ = Dispatcher.BeginInvoke(new Action(UpdateMemoryUI), DispatcherPriority.ApplicationIdle);
                return CommandResult.Ok(result);
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex)
            {
                if (_memStatusLabel != null) _memStatusLabel.Text = "清理失败: " + ex.Message;
                AppLog.Log("MemoryClean", ex);
                return CommandResult.Fail(CommandErrorCode.Failed, "内存清理失败：" + ex.Message);
            }
        }

        public void RestartAutoCleanTimer()
        {
            if (_autoCleanTimer != null) _autoCleanTimer.Stop();
            if (!AppPrefs.GetBool("AutoCleanEnabled", false)) return;
            // 每分钟滴答一次，每次判断是否需要清理。
            _autoCleanTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(60) };
            _autoCleanTimer.Tick += (s, e) => AutoCleanCheck();
            _autoCleanTimer.Start();
        }

        void AutoCleanCheck()
        {
            try
            {
                bool byTime = AppPrefs.GetBool("AutoCleanByTime", true);
                bool byTh = AppPrefs.GetBool("AutoCleanByThreshold", true);
                bool shouldClean = false;
                if (byTime)
                {
                    double mins; AppPrefs.GetDouble("AutoCleanMinutes", out mins);
                    if (mins <= 0) mins = 30;
                    if ((DateTime.Now - _lastCleanTime).TotalMinutes >= mins) shouldClean = true;
                }
                if (!shouldClean && byTh)
                {
                    double th; AppPrefs.GetDouble("AutoCleanThreshold", out th);
                    if (th <= 0) th = 80;
                    var ms = MemoryCleaner.GetStatus();
                    if (ms != null && ms.MemoryLoadPercent >= th) shouldClean = true;
                }
                if (shouldClean)
                {
                    _lastCleanTime = DateTime.Now;
                    var flags = MemoryCleaner.GetSavedFlags();
                    // 自动清理跳过可能导致卡顿的项，除非用户明确允许——后台 standby 清除可能让系统停滞。
                    if (!AppPrefs.GetBool("AutoCleanAllowFreezes", false))
                        flags &= ~(MemoryCleaner.CleanFlags.StandbyList | MemoryCleaner.CleanFlags.ModifiedPageList);
                    AppLog.Log("AutoClean", "triggered, flags=" + flags);
                    CleanMemory(flags);
                }
            }
            catch { }
        }
    }
}

