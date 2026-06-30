using System;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.ServiceProcess;
using Microsoft.Win32;

namespace PowerAudioManager
{
    public enum AutoStartMethod { None, Registry, ScheduledTask, Service }

    /// <summary>
    /// Manages OneBox auto-start across three mechanisms: Registry Run key,
    /// Task Scheduler logon trigger, and Windows Service (SYSTEM, auto-admin).
    /// Only one method is active at a time — enabling a new method disables the old.
    /// </summary>
    public static class AutoStartService
    {
        const string RegPath = @"SOFTWARE\Microsoft\Windows\CurrentVersion\Run";
        const string RegValue = "OneBox";
        const string TaskName = "OneBox";
        const string ServiceName = "OneBoxSvc";

        static string ExePath => Environment.ProcessPath;

        // ----- Detection ---------------------------------------------------

        public static AutoStartMethod GetCurrent()
        {
            if (IsServiceInstalled())
                return AutoStartMethod.Service;
            if (IsTaskInstalled())
                return AutoStartMethod.ScheduledTask;
            if (IsRegistrySet())
                return AutoStartMethod.Registry;
            return AutoStartMethod.None;
        }

        static bool IsRegistrySet()
        {
            try
            {
                using (var key = Registry.CurrentUser.OpenSubKey(RegPath, false))
                    return key?.GetValue(RegValue) != null;
            }
            catch { return false; }
        }

        static bool IsTaskInstalled()
        {
            try
            {
                var psi = new ProcessStartInfo
                {
                    FileName = "schtasks.exe",
                    Arguments = $"/query /tn \"{TaskName}\"",
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true
                };
                using (var p = Process.Start(psi))
                {
                    p.WaitForExit(5000);
                    return p.ExitCode == 0;
                }
            }
            catch { return false; }
        }

        static bool IsServiceInstalled()
        {
            try
            {
                using (var sc = new ServiceController(ServiceName))
                {
                    // The constructor does NOT throw for non-existent services.
                    // Touching any property (e.g. Status) forces a handle open and
                    // throws InvalidOperationException if the service is absent.
                    var _ = sc.Status;
                    return true;
                }
            }
            catch { return false; }
        }

        // ----- Enable / Disable -------------------------------------------

        /// <summary>Called by the elevated --elevate-autostart helper process.
        /// Applies the change with full admin rights and returns.</summary>
        public static string ApplyAutoStart(AutoStartMethod method)
        {
            return Enable(method); // runs as admin, so UAC-heavy ops will succeed
        }

        // Launch a brief elevated OneBox.exe process to apply an auto-start
        // change that needs admin (schtasks /rl highest, sc create/delete).
        // The UAC dialog shows "OneBox" since we launch ourselves with runas.
        static string LaunchElevatedHelper(int methodInt)
        {
            try
            {
                var psi = new ProcessStartInfo
                {
                    FileName = Environment.ProcessPath,
                    Arguments = $"--elevate-autostart {methodInt}",
                    Verb = "runas",
                    UseShellExecute = true
                };
                Process.Start(psi);
                return null; // the elevated process will handle it
            }
            catch (Win32Exception ex) when (ex.NativeErrorCode == 1223)
            { return "已取消 UAC 授权"; }
            catch (Exception ex) { return $"提权失败: {ex.Message}"; }
        }

        /// <summary>Enable one method. Returns null on success, error string on failure.</summary>
        public static string Enable(AutoStartMethod method)
        {
            // If the operation (enable + cleanup) needs admin and we don't have it,
            // launch a brief elevated OneBox.exe helper.  UAC shows "OneBox".
            if (!AdminUtils.IsAdmin())
            {
                bool needElevate = method == AutoStartMethod.ScheduledTask
                                || method == AutoStartMethod.Service
                                || IsTaskInstalled()
                                || IsServiceInstalled();
                if (needElevate)
                    return LaunchElevatedHelper((int)method);
            }

            // Always clean up the other methods first so only one is active.
            string cleanErr = DisableAll();
            if (cleanErr != null) return cleanErr;

            string err;
            switch (method)
            {
                case AutoStartMethod.None: err = null; break;
                case AutoStartMethod.Registry: err = EnableRegistry(); break;
                case AutoStartMethod.ScheduledTask: err = EnableTask(); break;
                case AutoStartMethod.Service: err = EnableService(); break;
                default: err = "未知方法"; break;
            }
            if (err == null && method != AutoStartMethod.None)
                AppPrefs.SetInt("AutoStart.LastMethod", (int)method);
            return err;
        }

        public static string Disable()
        {
            return DisableAll();
        }

        // ----- Registry ---------------------------------------------------

        static string EnableRegistry()
        {
            try
            {
                using (var key = Registry.CurrentUser.OpenSubKey(RegPath, true))
                {
                    if (key == null) return "无法访问注册表 Run 键";
                    key.SetValue(RegValue, ExePath);
                }
                AppLog.Log("AutoStart", "registry enabled");
                return null;
            }
            catch (Exception ex) { return $"注册表写入失败: {ex.Message}"; }
        }

        static void DisableRegistry()
        {
            try
            {
                using (var key = Registry.CurrentUser.OpenSubKey(RegPath, true))
                    key?.DeleteValue(RegValue, false);
            }
            catch { }
        }

        // ----- Task Scheduler ---------------------------------------------
        // schtasks /create /tn "OneBox" /tr "\"<path>\"" /sc onlogon /rl highest /f
        // /rl highest → runs with admin privileges (UAC approved once at creation).

        static string EnableTask()
        {
            if (!AdminUtils.IsAdmin())
                return LaunchElevatedHelper((int)AutoStartMethod.ScheduledTask);

            try
            {
                var psi = new ProcessStartInfo
                {
                    FileName = "schtasks.exe",
                    Arguments = $"/create /tn \"{TaskName}\" /tr \"\\\"{ExePath}\\\"\" /sc onlogon /rl highest /f",
                    UseShellExecute = false,
                    CreateNoWindow = true
                };
                using (var p = Process.Start(psi))
                {
                    p?.WaitForExit(10000);
                    if (p?.ExitCode != 0) return $"计划任务创建失败 (exit={p?.ExitCode})";
                }
                AppLog.Log("AutoStart", "task enabled");
                return null;
            }
            catch (Exception ex) { return $"计划任务创建失败: {ex.Message}"; }
        }

        static string DisableTask()
        {
            try
            {
                if (!IsTaskInstalled()) return null;
                var psi = new ProcessStartInfo
                {
                    FileName = "schtasks.exe",
                    Arguments = $"/delete /tn \"{TaskName}\" /f",
                    UseShellExecute = false,
                    CreateNoWindow = true
                };
                using (var p = Process.Start(psi))
                {
                    p?.WaitForExit(10000);
                    if (p?.ExitCode != 0) return $"计划任务删除失败 (exit={p?.ExitCode})";
                }
                return null;
            }
            catch (Exception ex) { return $"计划任务删除失败: {ex.Message}"; }
        }

        // ----- Service ----------------------------------------------------
        // Service runs as SYSTEM (auto-admin). The service binary is OneBox.exe
        // started with --service.  sc.exe manages service registration.

        static string EnableService()
        {
            if (!AdminUtils.IsAdmin())
                return LaunchElevatedHelper((int)AutoStartMethod.Service);

            try
            {
                // 1) Create the service
                var create = new ProcessStartInfo
                {
                    FileName = "sc.exe",
                    Arguments = $"create \"{ServiceName}\" binPath= \"\\\"{ExePath}\\\" --service\" start= auto",
                    UseShellExecute = false,
                    CreateNoWindow = true
                };
                using (var p = Process.Start(create))
                {
                    p?.WaitForExit(10000);
                    if (p?.ExitCode != 0)
                    {
                        AppLog.Log("AutoStart", $"sc create exit={p?.ExitCode}");
                        return $"服务创建失败 (exit={p?.ExitCode})。";
                    }
                }
                // 2) Set description
                try
                {
                    var desc = new ProcessStartInfo
                    {
                        FileName = "sc.exe",
                        Arguments = $"description \"{ServiceName}\" \"OneBox 桌面工具箱 — 开机自启服务\"",
                        UseShellExecute = false,
                        CreateNoWindow = true
                    };
                    Process.Start(desc)?.WaitForExit(3000);
                }
                catch { }
                // 3) Start the service
                try
                {
                    using (var sc = new ServiceController(ServiceName))
                    {
                        sc.Start();
                        sc.WaitForStatus(ServiceControllerStatus.Running, TimeSpan.FromSeconds(10));
                    }
                }
                catch (Exception ex) { AppLog.Log("AutoStart", $"svc start: {ex.Message}"); }
                AppLog.Log("AutoStart", "service enabled");
                return null;
            }
            catch (System.ComponentModel.Win32Exception ex) when (ex.NativeErrorCode == 1223)
            { return "已取消 UAC（服务安装需要一次管理员授权）"; }
            catch (Exception ex) { return $"服务安装失败: {ex.Message}"; }
        }

        static string DisableService()
        {
            try
            {
                if (!IsServiceInstalled()) return null;
                using (var sc = new ServiceController(ServiceName))
                {
                    try { if (sc.Status == ServiceControllerStatus.Running) { sc.Stop(); sc.WaitForStatus(ServiceControllerStatus.Stopped, TimeSpan.FromSeconds(10)); } }
                    catch { }
                }
            }
            catch { }
            try
            {
                var psi = new ProcessStartInfo
                {
                    FileName = "sc.exe",
                    Arguments = $"delete \"{ServiceName}\"",
                    UseShellExecute = false,
                    CreateNoWindow = true
                };
                using (var p = Process.Start(psi))
                {
                    p?.WaitForExit(10000);
                    if (p?.ExitCode != 0) return $"服务删除失败 (exit={p?.ExitCode})";
                }
                return null;
            }
            catch (Exception ex) { return $"服务删除失败: {ex.Message}"; }
        }

        // ----- Helpers ----------------------------------------------------

        /// <summary>Remove all auto-start methods. Returns the first error, or null.</summary>
        static string DisableAll()
        {
            string err = DisableService();
            if (err != null) return err;
            err = DisableTask();
            if (err != null) return err;
            DisableRegistry(); // never fails (no UAC needed)
            return null;
        }
    }
}
