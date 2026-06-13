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
        [STAThread]
        public static void Main(string[] args)
        {
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

}
