using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using System.IO;
using Microsoft.Win32;

namespace PowerAudioManager
{
    public class App : Application
    {
        static System.Threading.Mutex _singleInstance;
        static System.Threading.EventWaitHandle _activateEvent;

        // 由 AdminUtils.RestartAsAdmin() 在启动提权进程前调用，让新实例成为 Mutex 持有者，避免因旧 Mutex 直接退出。
        public static void ReleaseSingleInstance()
        {
            try { _singleInstance?.Dispose(); _singleInstance = null; } catch { }
            try { _activateEvent?.Dispose(); _activateEvent = null; } catch { }
        }

        [STAThread]
        public static void Main(string[] args)
        {
            // .NET 8 默认仅支持 ASCII/UTF-8/UTF-16；936 (GBK) 等代码页在
            // 未注册提供程序时会抛 NotSupportedException。powercfg OEM 输出和
            // 升级脚本依赖 GBK，必须在所有 Encoding 调用前注册。
            // .NET Framework 4 默认加载所有 Windows 代码页，这是迁移回归。
            try { System.Text.Encoding.RegisterProvider(System.Text.CodePagesEncodingProvider.Instance); } catch { }

            // --service: 以 Windows 服务模式运行，监听会话变化并在交互会话中启动 OneBox.exe（不带 --service）。
            if (args.Length > 0 && args[0] == "--service")
            {
                try { PowerAudioManager.OneBoxService.RunService(); } catch { }
                return;
            }

            // --elevate-autostart <method>: 提权辅助进程，应用开机自启设置（schtasks/sc 需管理员权限）后退出。
            // 非管理员 GUI 实例启动它，使 UAC 对话框显示 OneBox 名称。
            if (args.Length > 1 && args[0] == "--elevate-autostart")
            {
                if (int.TryParse(args[1], out int m) && m >= 0 && m <= 3)
                {
                    var method = (PowerAudioManager.AutoStartMethod)m;
                    string err = PowerAudioManager.AutoStartService.ApplyAutoStart(method);
                    if (err != null)
                        System.Windows.MessageBox.Show(err, "OneBox 开机自启", System.Windows.MessageBoxButton.OK, System.Windows.MessageBoxImage.Warning);
                }
                return;
            }

            // 后台预热内存 PerformanceCounter：.NET 8 冷启动首次构造 ~5s，GetStatus() 在 UI 线程执行，提前启动避免界面卡顿。
            try { PowerAudioManager.MemoryCleaner.WarmupCounters(); } catch { }

            // 单实例守护：第二个启动通过 Mutex.TryOpenExisting 检测已有实例，发信号激活窗口后退出。
            // 使用 TryOpenExisting（仅探测，不获取所有权）+ EventWaitHandle 唤醒第一个实例。
            if (System.Threading.Mutex.TryOpenExisting("Local\\OneBox-SingleInstance", out var _existingMutex))
            {
                _existingMutex.Dispose();
                try
                {
                    using (var ev = System.Threading.EventWaitHandle.OpenExisting("Local\\OneBox-Activate"))
                        ev.Set();
                }
                catch { }
                return;
            }
            _singleInstance = new System.Threading.Mutex(true, "Local\\OneBox-SingleInstance");
            _activateEvent = new System.Threading.EventWaitHandle(false,
                System.Threading.EventResetMode.AutoReset, "Local\\OneBox-Activate");

            AppDomain.CurrentDomain.UnhandledException += (s, ex) => {
                try { System.IO.File.WriteAllText(System.IO.Path.GetTempPath() + "pam_crash.log",
                    $"{DateTime.Now} UnhandledException: {ex.ExceptionObject}"); } catch { }
            };
            System.Windows.Forms.Application.ThreadException += (s, ex) => {
                try { System.IO.File.AppendAllText(System.IO.Path.GetTempPath() + "pam_crash.log",
                    $"{System.Environment.NewLine}{DateTime.Now} ThreadException: {ex.Exception}"); } catch { }
            };
            var app = new App();
            app.DispatcherUnhandledException += (s, ex) => { try { System.IO.File.AppendAllText(System.IO.Path.GetTempPath() + "pam_crash.log", $"{System.Environment.NewLine}{DateTime.Now} Dispatcher: {ex.Exception}"); } catch { } ex.Handled = true; };
            // MaterialDesign 深色 + 紫影主题 #8E8CD8，应用级作用域，所有窗口共享同一设计语言。
            // 必须在 new App() 之后（Application.Current 存在）、窗口构建之前执行。
            try { MaterialTheme.Apply(); } catch (Exception ex) { try { System.IO.File.AppendAllText(System.IO.Path.GetTempPath() + "pam_crash.log", $"{DateTime.Now} MaterialTheme: {ex}"); } catch { } }
            // global:: 前缀绕过 Application.MainWindow 属性名冲突
            var window = new global::PowerAudioManager.MainWindow();
            try { window.Show(); } catch (Exception ex) { try { System.IO.File.AppendAllText(System.IO.Path.GetTempPath() + "pam_crash.log", $"{DateTime.Now} Show: {ex}"); } catch { } throw; }
            // 后台线程监听第二个实例的激活信号，将已有窗口带到前台。
            var wref = new System.WeakReference<Window>(window);
            new System.Threading.Thread(() =>
            {
                while (_activateEvent != null)
                {
                    try
                    {
                        if (!_activateEvent.WaitOne(5000)) continue;
                        app.Dispatcher.BeginInvoke(new Action(() =>
                        {
                            try
                            {
                                if (wref.TryGetTarget(out var w) && w != null)
                                {
                                    if (w.WindowState == System.Windows.WindowState.Minimized)
                                        w.WindowState = System.Windows.WindowState.Normal;
                                    w.Show();
                                    w.Activate();
                                    w.Topmost = true;   // 强制置顶取巧
                                    w.Topmost = false;
                                }
                            }
                            catch { }
                        }));
                    }
                    catch { }
                }
            }) { IsBackground = true, Name = "ActivateListener" }.Start();
            try { app.Run(); } catch (Exception ex) { try { System.IO.File.AppendAllText(System.IO.Path.GetTempPath() + "pam_crash.log", $"{System.Environment.NewLine}{DateTime.Now} Run: {ex}"); } catch { } throw; }
        }
    }

    /// <summary>
    /// 轻量诊断日志，最佳努力，静默 catch，绝不抛异常。
    /// </summary>
    public static class AppLog
    {
        // 日志位于 exe 同目录，便于查找。若 exe 目录不可写则回退到 %TEMP%。
        static readonly string _path = ResolveLogPath();
        static readonly object _lock = new object();
        static string ResolveLogPath()
        {
            try
            {
                var dir = System.IO.Path.GetDirectoryName(Environment.ProcessPath);
                if (!string.IsNullOrEmpty(dir)) return System.IO.Path.Combine(dir, "OneBox.log");
            }
            catch { }
            return System.IO.Path.GetTempPath() + "OneBox.log";
        }

        public static void Log(string message)
        {
            try
            {
                lock (_lock)
                {
                    System.IO.File.AppendAllText(_path,
                        $"{DateTime.Now:yyyy-MM-dd HH:mm:ss} {message}{System.Environment.NewLine}");
                }
            }
            catch { }
        }
        public static void Log(string context, Exception ex)
        {
            Log(ex == null ? context : $"{context}: {ex.GetType().Name}: {ex.Message}");
        }
        public static void Log(string context, string detail)
        {
            Log($"[{context}] {detail}");
        }
    }

}
