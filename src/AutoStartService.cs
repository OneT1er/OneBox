using System;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.ServiceProcess;
using Microsoft.Win32;
using OneBox.Contracts;

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
        static readonly IServiceRegistrationOperations ServiceRegistration = new WindowsServiceRegistrationOperations();

        static string ExePath => Environment.ProcessPath;
        static string ServiceExePath => Path.Combine(AppContext.BaseDirectory, "OneBox.Service.exe");

        public static AutoStartMethod GetCurrent()
        {
            if (!IsEnabled()) return AutoStartMethod.None;
            int preferred = AppPrefs.GetInt("AutoStart.LastMethod", 0);
            if (preferred == (int)AutoStartMethod.Service && IsServiceInstalled()) return AutoStartMethod.Service;
            if (preferred == (int)AutoStartMethod.ScheduledTask && IsTaskInstalled()) return AutoStartMethod.ScheduledTask;
            if (preferred == (int)AutoStartMethod.Registry && IsRegistrySet()) return AutoStartMethod.Registry;
            // Compatibility for pre-contract installs that have no LastMethod value.
            if (IsServiceInstalled()) return AutoStartMethod.Service;
            if (IsTaskInstalled()) return AutoStartMethod.ScheduledTask;
            if (IsRegistrySet()) return AutoStartMethod.Registry;
            return AutoStartMethod.None;
        }

        public static bool IsEnabled()
        {
            // Missing flag preserves the previous install's effective state. Once written,
            // the preference is authoritative and service installation is capability only.
            return AppPrefs.GetBool("AutoStart.Enabled", IsServiceInstalled() || IsTaskInstalled() || IsRegistrySet());
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

        public static bool IsServiceInstalled()
        {
            return ServiceRegistration.IsInstalled;
        }

        public static bool IsServiceRunning()
        {
            try
            {
                using var controller = new ServiceController(ServiceName);
                return controller.Status == ServiceControllerStatus.Running;
            }
            catch { return false; }
        }

        public static ServiceImagePathKind GetServiceRegistrationKind()
        {
            return ServiceImagePath.Classify(ServiceRegistration.ImagePath, ServiceExePath);
        }

        public static string RepairService()
        {
            if (!AdminUtils.IsAdmin()) return LaunchElevatedCommand("--repair-service");
            return EnableService();
        }

        public static string ApplyAutoStart(AutoStartMethod method)
        {
            return Enable(method);
        }

        public static string PrepareForUpdate()
        {
            bool installed = IsServiceInstalled();
            try { UpdateServiceState.Begin(installed); }
            catch (Exception ex) { return "无法记录更新协调状态：" + ex.Message; }
            // Even without OneBoxSvc, reject an orphaned Hardware helper before Velopack
            // replaces the directory. A healthy no-service installation has no helper.
            if (!installed) return StopServiceForUpdate();
            if (!AdminUtils.IsAdmin()) return LaunchElevatedCommand("--prepare-update");
            return StopServiceForUpdate();
        }

        public static string StopServiceForUpdate()
        {
            try
            {
                if (IsServiceInstalled())
                {
                    using var controller = new ServiceController(ServiceName);
                    if (controller.Status != ServiceControllerStatus.Stopped)
                    {
                        if (controller.Status != ServiceControllerStatus.StopPending) controller.Stop();
                        controller.WaitForStatus(ServiceControllerStatus.Stopped, TimeSpan.FromSeconds(10));
                    }
                    if (controller.Status != ServiceControllerStatus.Stopped)
                        return "OneBoxSvc 未在超时前停止。";
                }

                DateTime deadline = DateTime.UtcNow.AddSeconds(3);
                while (DateTime.UtcNow < deadline)
                {
                    Process[] helpers = Process.GetProcessesByName("OneBox.Hardware");
                    try { if (helpers.Length == 0) return null; }
                    finally { foreach (Process helper in helpers) helper.Dispose(); }
                    System.Threading.Thread.Sleep(100);
                }
                Process[] remaining = Process.GetProcessesByName("OneBox.Hardware");
                try { return remaining.Length == 0 ? null : "OneBox.Hardware 未随服务停止，拒绝更新。"; }
                finally { foreach (Process helper in remaining) helper.Dispose(); }
            }
            catch (Exception ex) { return "停止更新相关服务失败：" + ex.Message; }
        }

        public static string VerifyStoppedForUpdate()
        {
            if (IsServiceRunning()) return "OneBoxSvc 仍在运行，拒绝让更新器修改目录。";
            try
            {
                Process[] helpers = Process.GetProcessesByName("OneBox.Hardware");
                try { return helpers.Length == 0 ? null : "OneBox.Hardware 仍在运行，拒绝让更新器修改目录。"; }
                finally { foreach (Process helper in helpers) helper.Dispose(); }
            }
            catch (Exception ex) { return "无法确认硬件进程已停止：" + ex.Message; }
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
                using Process process = Process.Start(psi);
                if (process == null) return "无法启动提权辅助进程";
                if (!process.WaitForExit(60000)) return "提权操作等待超时";
                return process.ExitCode == 0 ? null : $"提权操作失败 (exit={process.ExitCode})";
            }
            catch (Win32Exception ex) when (ex.NativeErrorCode == 1223)
            { return "已取消 UAC 授权"; }
            catch (Exception ex) { return $"提权失败: {ex.Message}"; }
        }

        static string LaunchElevatedCommand(string arguments)
        {
            try
            {
                using Process process = Process.Start(new ProcessStartInfo
                {
                    FileName = Environment.ProcessPath,
                    Arguments = arguments,
                    Verb = "runas",
                    UseShellExecute = true,
                });
                if (process == null) return "无法启动提权辅助进程";
                if (!process.WaitForExit(60000)) return "提权操作等待超时";
                return process.ExitCode == 0 ? null : $"提权操作失败 (exit={process.ExitCode})";
            }
            catch (Win32Exception ex) when (ex.NativeErrorCode == 1223) { return "已取消 UAC 授权"; }
            catch (Exception ex) { return $"提权失败: {ex.Message}"; }
        }

        public static string Enable(AutoStartMethod method)
        {
            // 如果操作（启用 + 清理）需要管理员权限而我们没有，
            // 则启动一个短暂的提权辅助进程。
            if (!AdminUtils.IsAdmin())
            {
                bool currentServiceReady = method == AutoStartMethod.Service
                    && IsServiceInstalled()
                    && GetServiceRegistrationKind() == ServiceImagePathKind.Current
                    && IsServiceRunning();
                bool needElevate = method == AutoStartMethod.ScheduledTask
                                || (method == AutoStartMethod.Service && !currentServiceReady)
                                || IsTaskInstalled();
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
                case AutoStartMethod.Service:
                    err = IsServiceInstalled()
                        && GetServiceRegistrationKind() == ServiceImagePathKind.Current
                        && IsServiceRunning()
                            ? null
                            : EnableService();
                    break;
                default: err = "未知方法"; break;
            }
            if (err == null)
            {
                if (!AppPrefs.SetBool("AutoStart.Enabled", method != AutoStartMethod.None))
                    return "开机自启设置已完成，但状态保存失败；请检查用户注册表权限后重试。";
                if (method != AutoStartMethod.None && !AppPrefs.SetInt("AutoStart.LastMethod", (int)method))
                    return "开机自启方式已完成，但状态保存失败；请检查用户注册表权限后重试。";
            }
            return err;
        }

        public static string Disable()
        {
            if (!AdminUtils.IsAdmin() && IsTaskInstalled())
                return LaunchElevatedCommand("--disable-autostart");
            string err = DisableStartupTriggers();
            if (err == null && !AppPrefs.SetBool("AutoStart.Enabled", false))
                return "开机自启已关闭，但状态保存失败；请检查用户注册表权限后重试。";
            return err;
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

        // 服务以 SYSTEM 身份运行。服务二进制是发布目录中的独立 OneBox.Service.exe。

        static string EnableService()
        {
            if (!AdminUtils.IsAdmin())
                return LaunchElevatedHelper((int)AutoStartMethod.Service);

            try
            {
                if (!File.Exists(ServiceExePath))
                    return $"未找到服务程序: {ServiceExePath}。请使用包含 OneBox.Service.exe 的完整发布目录。";

                var coordinator = new ServiceRegistrationCoordinator(ServiceRegistration);
                ServiceRegistrationResult registration = coordinator.Ensure(ServiceExePath);
                if (!registration.Success)
                    return registration.PreviousPathKind == ServiceImagePathKind.LegacyGui
                        ? $"检测到旧 OneBox.exe --service 注册，但迁移到 OneBox.Service.exe 失败 (exit={registration.ExitCode})。请关闭自启后重新安装服务。"
                        : $"服务安装或路径修复失败 (exit={registration.ExitCode})，当前路径: {ServiceRegistration.ImagePath}";
                if (registration.Action != ServiceRegistrationAction.None)
                    AppLog.Log("AutoStart", $"service registration action={registration.Action}");
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
                AppLog.Log("AutoStart", "service enabled");
                return null;
            }
            catch (System.ComponentModel.Win32Exception ex) when (ex.NativeErrorCode == 1223)
            { return "已取消 UAC（服务安装需要一次管理员授权）"; }
            catch (Exception ex) { return $"服务安装失败: {ex.Message}"; }
        }

        public static string UninstallService()
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
            return DisableStartupTriggers();
        }

        static string DisableStartupTriggers()
        {
            string err = DisableTask();
            if (err != null) return err;
            DisableRegistry();
            return null;
        }

    }
}
