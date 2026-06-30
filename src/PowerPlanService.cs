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

        // 执行 powercfg 并返回 stdout，超时/失败返回 null。用 BeginOutputReadLine 异步读
        // 以避免经典死锁（stdout 管道满时父进程在 ReadToEnd 阻塞子进程）。powercfg 输出小，
        // 实践中未触发但模式正确。用系统 OEM 代码页解码，非中文 Windows 也能正常解析。
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
                    proc.OutputDataReceived += (s, e) => { if (e.Data != null) sb.AppendLine(e.Data); };
                    proc.BeginOutputReadLine();
                    if (!proc.WaitForExit(5000)) { try { proc.Kill(); } catch { } return null; } // 不挂起刷新线程
                    proc.WaitForExit(); // 等待异步输出排空
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
                    // 超时保护：powercfg 在系统策略刷新时可能挂起，不能永久阻塞线程池线程。
                    // 超时视为失败。
                    if (!proc.WaitForExit(5000)) { try { proc.Kill(); } catch { } return false; }
                    return proc.ExitCode == 0;
                }
            }
            catch { return false; }
        }

        // 将 powercfg 调用（可能 1-3s）卸载到线程池，避免冻结 UI。onDone 通过 dispatcher 在 UI 线程执行。
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
