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
    public static class PowerPlanService
    {
        public static List<PowerPlanInfo> GetPowerPlans()
        {
            var plans = new List<PowerPlanInfo>();
            try
            {
                var output = RunPowercfg("/list");
                if (output == null) return plans;
                var activeGuid = GetActivePlanGuid();

                foreach (var line in output.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries))
                {
                    if (!line.Contains("GUID:")) continue;
                    int idx = line.IndexOf("GUID:");
                    if (idx < 0) continue;
                    var rest = line.Substring(idx + 5).Trim();
                    var guid = rest.Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries)[0];
                    var parenStart = line.LastIndexOf('(');
                    var parenEnd = line.LastIndexOf(')');
                    if (parenStart < 0 || parenEnd <= parenStart) continue;
                    var name = line.Substring(parenStart + 1, parenEnd - parenStart - 1);
                    plans.Add(new PowerPlanInfo
                    {
                        Guid = guid,
                        Name = name,
                        IsActive = guid.Equals(activeGuid, StringComparison.OrdinalIgnoreCase)
                    });
                }
            }
            catch (Exception ex) { AppLog.Log("PowerPlan", ex); }
            return plans;
        }

        // Run powercfg with the given args and return its stdout, or null on
        // timeout/failure. Reads stdout asynchronously (BeginOutputReadLine) to
        // avoid the classic deadlock where a full stdout pipe blocks the child
        // while the parent is still in ReadToEnd — powercfg output is tiny so
        // this never triggered in practice, but the pattern is correct. Output
        // is decoded with the system OEM code page so parsing works on
        // non-Chinese Windows too.
        static string RunPowercfg(string args)
        {
            try
            {
                var psi = new ProcessStartInfo("powercfg", args)
                {
                    RedirectStandardOutput = true,
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    StandardOutputEncoding = Encoding.GetEncoding(Native.GetOemCodePage())
                };
                using (var proc = Process.Start(psi))
                {
                    var sb = new StringBuilder();
                    // Capture asynchronously so a full pipe can't deadlock us.
                    proc.OutputDataReceived += (s, e) => { if (e.Data != null) sb.AppendLine(e.Data); };
                    proc.BeginOutputReadLine();
                    if (!proc.WaitForExit(5000)) { try { proc.Kill(); } catch { } return null; } // don't hang the refresher
                    proc.WaitForExit(); // let async drain finish
                    return sb.ToString();
                }
            }
            catch { return null; }
        }

        public static string GetActivePlanGuid()
        {
            var output = RunPowercfg("/getactivescheme");
            if (output == null) return "";
            var idx = output.IndexOf("GUID:");
            if (idx >= 0)
            {
                var sub = output.Substring(idx + 5).Trim();
                return sub.Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries)[0];
            }
            return "";
        }

        public static bool SetActivePlan(string planGuid)
        {
            try
            {
                var psi = new ProcessStartInfo("powercfg", "/setactive " + planGuid)
                {
                    UseShellExecute = false,
                    CreateNoWindow = true
                };
                using (var proc = Process.Start(psi))
                {
                    // Cap the wait like the readers above: if powercfg hangs during
                    // a system-wide policy refresh we must not leave a threadpool
                    // thread stuck forever (the async caller's onDone would never
                    // fire). Treat a timeout as failure.
                    if (!proc.WaitForExit(5000)) { try { proc.Kill(); } catch { } return false; }
                    return proc.ExitCode == 0;
                }
            }
            catch { return false; }
        }

        // Offload the (potentially 1-3s) powercfg call to a threadpool thread so the UI
        // dispatcher isn't frozen during the system-wide policy refresh. `onDone` runs on
        // the calling (UI) thread via dispatcher if one is supplied, otherwise inline.
        public static void SetActivePlanAsync(string planGuid, System.Windows.Threading.Dispatcher dispatcher, Action<bool> onDone)
        {
            System.Threading.ThreadPool.QueueUserWorkItem(_ =>
            {
                bool ok = SetActivePlan(planGuid);
                if (onDone == null) return;
                if (dispatcher == null) { onDone(ok); return; }
                dispatcher.BeginInvoke(new Action(() => onDone(ok)));
            });
        }
    }

}
