using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Win32;

namespace PowerAudioManager
{
    public enum UpdateServiceRepairAction { None, Start, MigrateAndStart }

    public static class UpdateServiceRepairPolicy
    {
        public static UpdateServiceRepairAction Decide(bool serviceWasInstalled, bool pendingRepair,
            OneBox.Contracts.ServiceImagePathKind pathKind, bool running)
        {
            if (!serviceWasInstalled || !pendingRepair) return UpdateServiceRepairAction.None;
            if (pathKind != OneBox.Contracts.ServiceImagePathKind.Current)
                return UpdateServiceRepairAction.MigrateAndStart;
            return running ? UpdateServiceRepairAction.None : UpdateServiceRepairAction.Start;
        }
    }

    internal static class UpdateServiceState
    {
        private const string RegistryPath = @"Software\PowerAudioManager\App";
        private const string ServiceWasInstalledName = "Update.ServiceWasInstalled";
        private const string PendingRepairName = "Update.PendingServiceRepair";
        private const string LastErrorName = "Update.LastServiceError";

        public static bool ServiceWasInstalled => Read(ServiceWasInstalledName) == "1";
        public static bool PendingRepair => Read(PendingRepairName) == "1";

        public static void Begin(bool serviceWasInstalled)
        {
            Write(ServiceWasInstalledName, serviceWasInstalled ? "1" : "0");
            Write(PendingRepairName, serviceWasInstalled ? "1" : "0");
            Write(LastErrorName, string.Empty);
        }

        public static void MarkPending() => Write(PendingRepairName, ServiceWasInstalled ? "1" : "0");

        public static void Complete()
        {
            Write(PendingRepairName, "0");
            Write(LastErrorName, string.Empty);
        }

        public static void Fail(string error)
        {
            Write(PendingRepairName, "1");
            Write(LastErrorName, error ?? "未知服务恢复错误");
        }

        private static string Read(string name)
        {
            using RegistryKey key = Registry.CurrentUser.OpenSubKey(RegistryPath, false);
            return key?.GetValue(name) as string;
        }

        private static void Write(string name, string value)
        {
            using RegistryKey key = Registry.CurrentUser.CreateSubKey(RegistryPath, true)
                ?? throw new InvalidOperationException("无法写入更新状态注册表。");
            key.SetValue(name, value ?? string.Empty, RegistryValueKind.String);
        }
    }

    internal sealed class UpdateServiceCoordinator : IUpdateApplicationCoordinator
    {
        public Task<UpdateCoordinationResult> PrepareAsync(CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(ToResult(AutoStartService.PrepareForUpdate()));
        }

        public Task<UpdateCoordinationResult> RecoverAsync(CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(ToResult(UpdateLifecycleHooks.CompletePendingServiceRepair()));
        }

        private static UpdateCoordinationResult ToResult(string error) =>
            error == null ? UpdateCoordinationResult.Ok() : UpdateCoordinationResult.Fail(error);
    }

    internal static class UpdateLifecycleHooks
    {
        private static string ServiceExecutablePath =>
            Path.Combine(AppContext.BaseDirectory, "OneBox.Service.exe");

        public static void BeforeUpdateFastCallback()
        {
            if (!UpdateServiceState.ServiceWasInstalled) return;
            string error = AutoStartService.VerifyStoppedForUpdate();
            if (error == null) return;
            UpdateServiceState.Fail(error);
            AppLog.Log("Update hook before", error);
            throw new InvalidOperationException(error);
        }

        public static void AfterUpdateFastCallback()
        {
            if (!UpdateServiceState.ServiceWasInstalled) return;
            if (!File.Exists(ServiceExecutablePath))
            {
                string error = "更新后的 OneBox.Service.exe 缺失，拒绝完成服务迁移。";
                UpdateServiceState.Fail(error);
                AppLog.Log("Update hook after", error);
                throw new FileNotFoundException(error, ServiceExecutablePath);
            }
            UpdateServiceState.MarkPending();
        }

        public static void Restarted()
        {
            string error = CompletePendingServiceRepair();
            if (error == null) return;
            AppLog.Log("Update hook restarted", error);
            throw new InvalidOperationException(error);
        }

        public static string RetryPendingServiceRepair()
        {
            if (!UpdateServiceState.PendingRepair) return null;
            string error = CompletePendingServiceRepair();
            if (error != null) AppLog.Log("Update service retry", error);
            return error;
        }

        public static string CompletePendingServiceRepair()
        {
            bool wasInstalled = UpdateServiceState.ServiceWasInstalled;
            bool pending = UpdateServiceState.PendingRepair;
            UpdateServiceRepairAction action = UpdateServiceRepairPolicy.Decide(
                wasInstalled, pending, AutoStartService.GetServiceRegistrationKind(), AutoStartService.IsServiceRunning());
            if (!wasInstalled || !pending || action == UpdateServiceRepairAction.None)
            {
                UpdateServiceState.Complete();
                return null;
            }

            string error = AutoStartService.RepairService();
            if (error == null && !AutoStartService.IsServiceRunning())
                error = "OneBoxSvc 修复后未进入 Running 状态。";
            if (error == null)
            {
                UpdateServiceState.Complete();
                return null;
            }
            UpdateServiceState.Fail(error);
            return error;
        }
    }
}
