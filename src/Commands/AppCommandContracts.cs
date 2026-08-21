using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace PowerAudioManager.Commands
{
    public enum AppCommandId
    {
        WindowShow,
        WindowHide,
        WindowSetCollapsed,
        AppExit,
        SettingsOpen,
        PowerList,
        PowerActivate,
        PowerCycle,
        AudioList,
        AudioActivate,
        AudioCycle,
        AudioSetVolume,
        AudioSetMute,
        MemoryClean,
        TranslateText,
        TranslateImageRegion,
        TranslateImageClipboard,
        ScreenshotForeground,
        ScreenshotOpenGallery,
        ClipboardOpen,
        ClipboardClear,
        LauncherShow,
        LauncherAdd,
        LauncherRemove,
        LauncherLaunch,
        MonitorChartOpen,
        MonitorStart,
        MonitorStop,
        UpdateCheck,
        AutoStartApply,
        RuntimeRefreshHotkeys,
        RuntimeRestartAutoClean,
        RuntimeApplyGeneral,
        RuntimeRebuildModules
    }

    public enum CommandSource
    {
        MainWindow,
        Tray,
        Hotkey,
        Settings,
        Launcher,
        System
    }

    public enum CommandErrorCode
    {
        None,
        UnknownCommand,
        InvalidPayload,
        Busy,
        Cancelled,
        NotAvailable,
        Rejected,
        Failed
    }

    public sealed class CommandRequest
    {
        public CommandRequest(AppCommandId commandId, CommandSource source, object payload = null,
            CancellationToken cancellationToken = default)
        {
            CommandId = commandId;
            Source = source;
            Payload = payload;
            CancellationToken = cancellationToken;
        }

        public AppCommandId CommandId { get; }
        public CommandSource Source { get; }
        public object Payload { get; }
        public CancellationToken CancellationToken { get; }

        public T RequirePayload<T>()
        {
            if (Payload is T typed) return typed;
            throw new AppCommandPayloadException(CommandId, typeof(T), Payload?.GetType());
        }
    }

    public sealed class CommandResult
    {
        CommandResult(bool success, CommandErrorCode errorCode, string userMessage, object data)
        {
            Success = success;
            ErrorCode = errorCode;
            UserMessage = userMessage ?? string.Empty;
            Data = data;
        }

        public bool Success { get; }
        public CommandErrorCode ErrorCode { get; }
        public string UserMessage { get; }
        public object Data { get; }
        public bool IsCancelled => ErrorCode == CommandErrorCode.Cancelled;

        public static CommandResult Ok(object data = null, string userMessage = "") =>
            new CommandResult(true, CommandErrorCode.None, userMessage, data);

        public static CommandResult Fail(CommandErrorCode errorCode, string userMessage, object data = null) =>
            new CommandResult(false, errorCode, userMessage, data);

        public static CommandResult Cancelled() =>
            new CommandResult(false, CommandErrorCode.Cancelled, string.Empty, null);
    }

    public sealed class AppCommandPayloadException : Exception
    {
        public AppCommandPayloadException(AppCommandId id, Type expected, Type actual)
            : base($"命令 {id} 需要 {expected.Name}，实际为 {actual?.Name ?? "null"}。") { }
    }

    public interface IAppCommandDispatcher
    {
        Task<CommandResult> DispatchAsync(CommandRequest request);
        IReadOnlyCollection<AppCommandId> RegisteredCommandIds { get; }
    }

    public sealed record WindowCollapsedPayload(bool Collapsed);
    public sealed record SettingsOpenPayload(int TabIndex = 0);
    public sealed record PowerActivatePayload(string PlanId);
    public sealed record AudioActivatePayload(string DeviceId);
    public sealed record AudioVolumePayload(float Volume);
    public sealed record AudioMutePayload(bool Muted);
    public sealed record MemoryCleanPayload(MemoryCleaner.CleanFlags Flags);
    public sealed record TextTranslatePayload(string Text);
    public sealed record ClipboardOpenPayload(int? ScreenX = null, int? ScreenY = null);
    public sealed record LauncherAddPayload(IReadOnlyList<string> Paths);
    public sealed record LauncherRemovePayload(int SlotIndex);
    public sealed record LauncherLaunchPayload(int SlotIndex);
    public sealed record UpdateCheckPayload(bool Manual);
    public sealed record AutoStartApplyPayload(bool Enabled, int PreferredMethod);
    public sealed record GeneralRuntimePayload(bool Topmost, bool LockPosition, bool RefreshFont,
        bool ResetScale = false, double? ManualScale = null);
}
