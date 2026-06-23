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
            // Single-instance guard: a second launch (e.g. double-clicking the exe
            // or the autostart entry firing while already running) would otherwise
            // spawn a second floating window, a second tray icon, and re-register
            // the same global hotkeys (which silently fail). Bail out instead.
            bool createdNew;
            _singleInstance = new System.Threading.Mutex(true, "Local\\OneBox-SingleInstance", out createdNew);
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
                    DateTime.Now + " UnhandledException: " + ex.ExceptionObject); } catch { }
            };
            System.Windows.Forms.Application.ThreadException += (s, ex) => {
                try { System.IO.File.AppendAllText(System.IO.Path.GetTempPath() + "pam_crash.log",
                    System.Environment.NewLine + DateTime.Now + " ThreadException: " + ex.Exception); } catch { }
            };
            var app = new App();
            app.DispatcherUnhandledException += (s, ex) => { try { System.IO.File.AppendAllText(System.IO.Path.GetTempPath() + "pam_crash.log", System.Environment.NewLine + DateTime.Now + " Dispatcher: " + ex.Exception); } catch { } ex.Handled = true; };
            // global:: prefix bypasses Application.MainWindow property name collision
            var window = new global::PowerAudioManager.MainWindow();
            try { window.Show(); } catch (Exception ex) { try { System.IO.File.AppendAllText(System.IO.Path.GetTempPath() + "pam_crash.log", DateTime.Now + " Show: " + ex); } catch { } throw; }
            try { app.Run(); } catch (Exception ex) { try { System.IO.File.AppendAllText(System.IO.Path.GetTempPath() + "pam_crash.log", System.Environment.NewLine + DateTime.Now + " Run: " + ex); } catch { } throw; }
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
                var dir = System.IO.Path.GetDirectoryName(System.Reflection.Assembly.GetExecutingAssembly().Location);
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
                        DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss") + " " + message + System.Environment.NewLine);
                }
            }
            catch { }
        }
        public static void Log(string context, Exception ex)
        {
            Log(context + (ex == null ? "" : (": " + ex.GetType().Name + ": " + ex.Message)));
        }
        // Structured tag + detail, e.g. Log("Screenshot", "source=CopyFromScreen app=chrome saved=...").
        public static void Log(string context, string detail)
        {
            Log("[" + context + "] " + detail);
        }
    }

}
