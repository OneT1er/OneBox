using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using System.IO;

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
            CleanMemory(MemoryCleaner.GetSavedFlags());
        }

        internal void CleanMemory(MemoryCleaner.CleanFlags flags)
        {
            if (_memStatusLabel != null) _memStatusLabel.Text = "正在清理...";
            System.Threading.ThreadPool.QueueUserWorkItem(state =>
            {
                // 非管理员：命令服务（OneBoxSvc）执行清理，无 UAC
                if (!AdminUtils.IsAdmin())
                {
                    Exception err = null;
                    ulong freedBytes = 0;
                    try
                    {
                        using (var client = new System.IO.Pipes.NamedPipeClientStream(".", "Global\\OneBox\\MemClean", System.IO.Pipes.PipeDirection.InOut))
                        {
                            client.Connect(8000);
                            using (var bw = new System.IO.BinaryWriter(client, System.Text.Encoding.UTF8, true)) { bw.Write((int)flags); bw.Flush(); }
                            using (var br = new System.IO.BinaryReader(client, System.Text.Encoding.UTF8, true)) freedBytes = br.ReadUInt64();
                        }
                    }
                    catch (Exception ex) { err = ex; }
                    Dispatcher.BeginInvoke(new Action(() =>
                    {
                        if (err != null) { if (_memStatusLabel != null) _memStatusLabel.Text = "清理失败: " + err.Message; return; }
                        if (_memStatusLabel != null) _memStatusLabel.Text = string.Format("已释放 {0:0} MB（服务清理）", freedBytes / 1024.0 / 1024.0);
                        AppLog.Log("MemoryClean", "service freed=" + (int)(freedBytes / 1024 / 1024) + "MB");
                        Dispatcher.BeginInvoke(new Action(UpdateMemoryUI), System.Windows.Threading.DispatcherPriority.ApplicationIdle);
                    }));
                    return;
                }

                // 管理员：直接清理
                MemoryCleaner.CleanResult r = null;
                Exception err2 = null;
                try { r = MemoryCleaner.CleanAll(flags); }
                catch (Exception ex) { err2 = ex; }
                Dispatcher.BeginInvoke(new Action(() =>
                {
                    if (err2 != null)
                    {
                        if (_memStatusLabel != null) _memStatusLabel.Text = "清理失败: " + err2.Message;
                        AppLog.Log("MemoryClean", "error: " + err2.Message);
                        return;
                    }
                    if (r != null && _memStatusLabel != null)
                    {
                        double freedMb = r.FreedBytes / 1024.0 / 1024.0;
                        _memStatusLabel.Text = string.Format("已释放 {0:0} MB", freedMb);
                        AppLog.Log("MemoryClean", "freed=" + (int)freedMb + "MB flags=" + flags);
                        Dispatcher.BeginInvoke(new Action(UpdateMemoryUI), System.Windows.Threading.DispatcherPriority.ApplicationIdle);
                    }
                }));
            });
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

