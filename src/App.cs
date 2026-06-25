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

            // Warm up the memory PerformanceCounters on a background thread ASAP.
            // Constructing them takes ~5s on .NET 8 cold start and GetStatus() runs on
            // the UI thread, so starting this before the window builds keeps startup
            // from freezing while the counters initialize in the background.
            try { PowerAudioManager.MemoryCleaner.WarmupCounters(); } catch { }

            // Single-instance guard: a second launch (e.g. double-clicking the exe
            // or the autostart entry firing while already running) would otherwise
            // spawn a second floating window, a second tray icon, and re-register
            // the same global hotkeys (which silently fail). Bail out instead.
            _singleInstance = new System.Threading.Mutex(true, "Local\\OneBox-SingleInstance", out var createdNew);
            if (!createdNew)
            {
                // Another instance owns the mutex. A previous owner that crashed
                // leaves an abandoned mutex — in that case we still take it.
                try { _singleInstance.WaitOne(0); createdNew = true; }
                catch (System.Threading.AbandonedMutexException) { createdNew = true; }
                if (!createdNew) return;
            }

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
            // global:: prefix bypasses Application.MainWindow property name collision
            var window = new global::PowerAudioManager.MainWindow();
            try { window.Show(); } catch (Exception ex) { try { System.IO.File.AppendAllText(System.IO.Path.GetTempPath() + "pam_crash.log", $"{DateTime.Now} Show: {ex}"); } catch { } throw; }
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
