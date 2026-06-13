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
                var psi = new ProcessStartInfo("powercfg", "/list")
                {
                    RedirectStandardOutput = true,
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    StandardOutputEncoding = Encoding.GetEncoding(936)
                };
                using (var proc = Process.Start(psi))
                {
                    var output = proc.StandardOutput.ReadToEnd();
                    proc.WaitForExit();
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
            }
            catch { }
            return plans;
        }

        public static string GetActivePlanGuid()
        {
            try
            {
                var psi = new ProcessStartInfo("powercfg", "/getactivescheme")
                {
                    RedirectStandardOutput = true,
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    StandardOutputEncoding = Encoding.GetEncoding(936)
                };
                using (var proc = Process.Start(psi))
                {
                    var output = proc.StandardOutput.ReadToEnd();
                    proc.WaitForExit();
                    var idx = output.IndexOf("GUID:");
                    if (idx >= 0)
                    {
                        var sub = output.Substring(idx + 5).Trim();
                        return sub.Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries)[0];
                    }
                }
            }
            catch { }
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
                    proc.WaitForExit();
                    return proc.ExitCode == 0;
                }
            }
            catch { return false; }
        }
    }

}
