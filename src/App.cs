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

        // Called by AdminUtils.RestartAsAdmin() before spawning the elevated process
        // so the new instance becomes the owner instead of bailing on the stale mutex.
        public static void ReleaseSingleInstance()
        {
            try { _singleInstance?.Dispose(); _singleInstance = null; } catch { }
            try { _activateEvent?.Dispose(); _activateEvent = null; } catch { }
        }

        [STAThread]
        public static void Main(string[] args)
        {
            // .NET 8 ships only ASCII/UTF-8/UTF-16 by default; code pages like 936
            // (GBK, used for powercfg OEM output and the updater .bat) throw
            // NotSupportedException unless the provider is registered. Do this first,
            // before anything touches Encoding. (On .NET Framework 4 all Windows code
            // pages were available by default — this is the migration regression that
            // broke power-plan detection and in-app updates.)
            try { System.Text.Encoding.RegisterProvider(System.Text.CodePagesEncodingProvider.Instance); } catch { }

            // --service flag: run as a Windows Service (session-login launcher),
            // not as the normal GUI app. The service monitors session changes and
            // spawns OneBox.exe (without --service) in each interactive session.
            if (args.Length > 0 && args[0] == "--service")
            {
                try { PowerAudioManager.OneBoxService.RunService(); } catch { }
                return;
            }

            // Warm up the memory PerformanceCounters on a background thread ASAP.
            // Constructing them takes ~5s on .NET 8 cold start and GetStatus() runs on
            // the UI thread, so starting this before the window builds keeps startup
            // from freezing while the counters initialize in the background.
            try { PowerAudioManager.MemoryCleaner.WarmupCounters(); } catch { }

            // Single-instance guard: a second launch activates the existing window
            // and exits, instead of spawning a second floating window + tray icon.
            // Uses Mutex.TryOpenExisting (performs an open-only probe — no ownership
            // ambiguity) followed by EventWaitHandle to wake the first instance.
            if (System.Threading.Mutex.TryOpenExisting("Local\\OneBox-SingleInstance", out var _existingMutex))
            {
                _existingMutex.Dispose();
                // Signal the existing instance to activate its window.
                try
                {
                    using (var ev = System.Threading.EventWaitHandle.OpenExisting("Local\\OneBox-Activate"))
                        ev.Set();
                }
                catch { }
                return;
            }
            // We are the first instance — create the guard mutex and hold it forever.
            _singleInstance = new System.Threading.Mutex(true, "Local\\OneBox-SingleInstance");
            // Create the activation event that second instances will signal.
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
            // Bootstrap MaterialDesign (dark + 紫影 #8E8CD8) at application scope so
            // every window — floating card and dialogs — shares one design language.
            // Must run after `new App()` (Application.Current exists) and before the
            // window is built, so the floating window resolves the styles on construct.
            try { MaterialTheme.Apply(); } catch (Exception ex) { try { System.IO.File.AppendAllText(System.IO.Path.GetTempPath() + "pam_crash.log", $"{DateTime.Now} MaterialTheme: {ex}"); } catch { } }
            // global:: prefix bypasses Application.MainWindow property name collision
            var window = new global::PowerAudioManager.MainWindow();
            try { window.Show(); } catch (Exception ex) { try { System.IO.File.AppendAllText(System.IO.Path.GetTempPath() + "pam_crash.log", $"{DateTime.Now} Show: {ex}"); } catch { } throw; }
            // Background listener: when a second instance starts, it signals
            // _activateEvent; we bring the existing window to the foreground.
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
                                    w.Topmost = true;   // force-to-foreground trick
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
    /// Lightweight best-effort diagnostic logger. Appends one line per call to
    /// %TEMP%\pam_debug.log so that silent catch blocks leave a trace. Never throws.
    /// </summary>
    public static class AppLog
    {
        // Log lives next to the exe so it's easy to find alongside OneBox.exe.
        // Falls back to %TEMP% if the exe dir isn't writable.
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
        // Structured tag + detail, e.g. Log("Screenshot", "source=CopyFromScreen app=chrome saved=...").
        public static void Log(string context, string detail)
        {
            Log($"[{context}] {detail}");
        }
    }

}
