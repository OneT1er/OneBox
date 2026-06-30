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
                    // ServiceController 构造函数不会对不存在的服务抛异常。
                    // 访问任何属性（如 Status）才会打开句柄，服务不存在时抛出 InvalidOperationException。
                    var _ = sc.Status;
                    return true;
                }
            }
            catch { return false; }
        }

        public static string ApplyAutoStart(AutoStartMethod method)
        {
            return Enable(method);
        }

        // 启动一个短暂的提权 OneBox.exe 进程来执行需要管理员权限的自启动操作
        // （schtasks /rl highest、sc create/delete）。
        // UAC 弹窗显示 "OneBox"，因为使用 runas 启动自身。
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
                return null;
            }
            catch (Win32Exception ex) when (ex.NativeErrorCode == 1223)
            { return "已取消 UAC 授权"; }
            catch (Exception ex) { return $"提权失败: {ex.Message}"; }
        }

        public static string Enable(AutoStartMethod method)
        {
            // 如果操作（启用 + 清理）需要管理员权限而我们没有，
            // 则启动一个短暂的提权辅助进程。
            if (!AdminUtils.IsAdmin())
            {
                bool needElevate = method == AutoStartMethod.ScheduledTask
                                || method == AutoStartMethod.Service
                                || IsTaskInstalled()
                                || IsServiceInstalled();
                if (needElevate)
                    return LaunchElevatedHelper((int)method);
            }

            // 先清理所有其他自启方式，确保同时只有一种生效。
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

        // schtasks /create /tn "OneBox" /tr "\"<path>\"" /sc onlogon /rl highest /f
        // /rl highest → 以管理员权限运行（创建时通过一次 UAC 授权）。

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

        // 服务以 SYSTEM 身份运行（自动管理员）。服务二进制就是 OneBox.exe，
        // 通过 --service 参数启动。sc.exe 管理服务注册。

        static string EnableService()
        {
            if (!AdminUtils.IsAdmin())
                return LaunchElevatedHelper((int)AutoStartMethod.Service);

            try
            {
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

        static string DisableAll()
        {
            string err = DisableService();
            if (err != null) return err;
            err = DisableTask();
            if (err != null) return err;
            DisableRegistry(); // 注册表操作无需 UAC，不会失败
            return null;
        }
    }
}
