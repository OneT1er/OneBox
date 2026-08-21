using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using PowerAudioManager;
using PowerAudioManager.Commands;
using Xunit;

namespace OneBox.Tests;

public sealed class CommandCatalogTests
{
    public static IEnumerable<object[]> EveryCommand() =>
        Enum.GetValues<AppCommandId>().Select(id => new object[] { id });

    [Theory]
    [MemberData(nameof(EveryCommand))]
    public void EveryCommandId_HasExactlyOneDefinition(AppCommandId id)
    {
        Assert.Equal(id, AppCommandCatalog.Get(id).Id);
        Assert.Single(AppCommandCatalog.All, definition => definition.Id == id);
    }

    [Theory]
    [MemberData(nameof(EveryCommand))]
    public void Dispatcher_RegistersEveryCommandId(AppCommandId id)
    {
        var dispatcher = new AppCommandDispatcher(_ => Task.FromResult(CommandResult.Ok()));

        Assert.Contains(id, dispatcher.RegisteredCommandIds);
    }

    [Theory]
    [MemberData(nameof(EveryCommand))]
    public async Task EveryCommand_WithCorrectPayload_ReachesHandler(AppCommandId id)
    {
        var definition = AppCommandCatalog.Get(id);
        bool reached = false;
        var dispatcher = new AppCommandDispatcher(_ =>
        {
            reached = true;
            return Task.FromResult(CommandResult.Ok());
        });

        var result = await dispatcher.DispatchAsync(new CommandRequest(id, CommandSource.System,
            CreatePayload(definition.PayloadType)));

        Assert.True(result.Success);
        Assert.True(reached);
    }

    [Fact]
    public async Task UnknownEnum_IsRejectedWithoutCallingHandler()
    {
        bool reached = false;
        var dispatcher = new AppCommandDispatcher(_ =>
        {
            reached = true;
            return Task.FromResult(CommandResult.Ok());
        });

        var result = await dispatcher.DispatchAsync(new CommandRequest((AppCommandId)9999,
            CommandSource.System));

        Assert.False(result.Success);
        Assert.Equal(CommandErrorCode.UnknownCommand, result.ErrorCode);
        Assert.False(reached);
    }

    [Fact]
    public async Task TypedCommand_MissingPayload_IsRejected()
    {
        var dispatcher = new AppCommandDispatcher(_ => Task.FromResult(CommandResult.Ok()));

        var result = await dispatcher.DispatchAsync(new CommandRequest(AppCommandId.PowerActivate,
            CommandSource.MainWindow));

        Assert.Equal(CommandErrorCode.InvalidPayload, result.ErrorCode);
    }

    [Fact]
    public async Task PayloadFreeCommand_UnexpectedPayload_IsRejected()
    {
        var dispatcher = new AppCommandDispatcher(_ => Task.FromResult(CommandResult.Ok()));

        var result = await dispatcher.DispatchAsync(new CommandRequest(AppCommandId.PowerCycle,
            CommandSource.Hotkey, new object()));

        Assert.Equal(CommandErrorCode.InvalidPayload, result.ErrorCode);
    }

    [Fact]
    public async Task CancelledBeforeDispatch_DoesNotReachHandlerOrShowAnErrorMessage()
    {
        bool reached = false;
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        var dispatcher = new AppCommandDispatcher(_ =>
        {
            reached = true;
            return Task.FromResult(CommandResult.Ok());
        });

        var result = await dispatcher.DispatchAsync(new CommandRequest(AppCommandId.ScreenshotForeground,
            CommandSource.Hotkey, cancellationToken: cancellation.Token));

        Assert.True(result.IsCancelled);
        Assert.Empty(result.UserMessage);
        Assert.False(reached);
    }

    [Fact]
    public async Task MatchingCancellationException_IsMappedToCancelled()
    {
        using var cancellation = new CancellationTokenSource();
        var dispatcher = new AppCommandDispatcher(async request =>
        {
            cancellation.Cancel();
            await Task.Delay(10, request.CancellationToken);
            return CommandResult.Ok();
        });

        var result = await dispatcher.DispatchAsync(new CommandRequest(AppCommandId.ScreenshotForeground,
            CommandSource.Hotkey, cancellationToken: cancellation.Token));

        Assert.Equal(CommandErrorCode.Cancelled, result.ErrorCode);
    }

    [Fact]
    public async Task HandlerException_IsMappedToStructuredFailure()
    {
        var dispatcher = new AppCommandDispatcher(_ => throw new InvalidOperationException("boom"));

        var result = await dispatcher.DispatchAsync(new CommandRequest(AppCommandId.PowerCycle,
            CommandSource.Hotkey));

        Assert.Equal(CommandErrorCode.Failed, result.ErrorCode);
        Assert.Contains("boom", result.UserMessage);
    }

    [Fact]
    public async Task ReentrantScreenshot_IsRejectedAsBusy()
    {
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var dispatcher = new AppCommandDispatcher(async _ =>
        {
            entered.SetResult();
            await release.Task;
            return CommandResult.Ok();
        });
        Task<CommandResult> first = dispatcher.DispatchAsync(new CommandRequest(
            AppCommandId.ScreenshotForeground, CommandSource.Hotkey));
        await entered.Task;

        var second = await dispatcher.DispatchAsync(new CommandRequest(
            AppCommandId.ScreenshotForeground, CommandSource.MainWindow));
        release.SetResult();

        Assert.Equal(CommandErrorCode.Busy, second.ErrorCode);
        Assert.True((await first).Success);
    }

    [Fact]
    public async Task Exit_IsOneShot()
    {
        var dispatcher = new AppCommandDispatcher(_ => Task.FromResult(CommandResult.Ok()));

        var first = await dispatcher.DispatchAsync(new CommandRequest(AppCommandId.AppExit, CommandSource.Tray));
        var second = await dispatcher.DispatchAsync(new CommandRequest(AppCommandId.AppExit, CommandSource.Tray));

        Assert.True(first.Success);
        Assert.Equal(CommandErrorCode.Busy, second.ErrorCode);
    }

    static object CreatePayload(Type type)
    {
        if (type == null) return null;
        if (type == typeof(WindowCollapsedPayload)) return new WindowCollapsedPayload(true);
        if (type == typeof(SettingsOpenPayload)) return new SettingsOpenPayload();
        if (type == typeof(PowerActivatePayload)) return new PowerActivatePayload("plan");
        if (type == typeof(AudioActivatePayload)) return new AudioActivatePayload("device");
        if (type == typeof(AudioVolumePayload)) return new AudioVolumePayload(0.5f);
        if (type == typeof(AudioMutePayload)) return new AudioMutePayload(true);
        if (type == typeof(MemoryCleanPayload)) return new MemoryCleanPayload(MemoryCleaner.CleanFlags.None);
        if (type == typeof(TextTranslatePayload)) return new TextTranslatePayload("text");
        if (type == typeof(ClipboardOpenPayload)) return new ClipboardOpenPayload();
        if (type == typeof(LauncherAddPayload)) return new LauncherAddPayload(new[] { "path" });
        if (type == typeof(LauncherRemovePayload)) return new LauncherRemovePayload(0);
        if (type == typeof(LauncherLaunchPayload)) return new LauncherLaunchPayload(0);
        if (type == typeof(UpdateCheckPayload)) return new UpdateCheckPayload(false);
        if (type == typeof(AutoStartApplyPayload)) return new AutoStartApplyPayload(false, 0);
        if (type == typeof(GeneralRuntimePayload)) return new GeneralRuntimePayload(false, false, false);
        throw new InvalidOperationException("Test payload missing for " + type.Name);
    }
}

public sealed class HotkeyDefinitionTests
{
    [Fact]
    public void NativeIds_AreUnique() =>
        Assert.Equal(HotkeyDefinitions.All.Count, HotkeyDefinitions.All.Select(x => x.NativeId).Distinct().Count());

    [Fact]
    public void PreferenceKeys_AreUniqueIgnoringFixedHotkey()
    {
        var keys = HotkeyDefinitions.All.Where(x => x.PreferenceKey != null).Select(x => x.PreferenceKey).ToArray();
        Assert.Equal(keys.Length, keys.Distinct(StringComparer.Ordinal).Count());
    }

    [Fact]
    public void FixedTextTranslation_IsExplicitlyRegistered()
    {
        var definition = Assert.Single(HotkeyDefinitions.All,
            x => x.CommandId == AppCommandId.TranslateText);
        Assert.Null(definition.PreferenceKey);
        Assert.Equal(0xBFFF, definition.NativeId);
        HotkeyDefinitions.Decode(definition.DefaultEncoded, out uint modifiers, out uint key);
        Assert.Equal(0x2u | 0x4u, modifiers);
        Assert.Equal(0x54u, key);
    }

    [Theory]
    [InlineData(AppCommandId.ScreenshotForeground, "Screenshot.Hotkey")]
    [InlineData(AppCommandId.ClipboardOpen, "Clipboard.Hotkey")]
    [InlineData(AppCommandId.TranslateImageRegion, "Screenshot.ImageTranslateHotkey")]
    [InlineData(AppCommandId.AudioCycle, "Audio.CycleHotkey")]
    [InlineData(AppCommandId.PowerCycle, "Power.CycleHotkey")]
    public void ConfigurableHotkey_PreservesLegacyPreferenceKey(AppCommandId id, string legacyKey)
    {
        Assert.Equal(legacyKey, Assert.Single(HotkeyDefinitions.All, x => x.CommandId == id).PreferenceKey);
    }
}

public sealed class PreferenceDefinitionTests
{
    [Theory]
    [InlineData("Screenshot.Hotkey")]
    [InlineData("Clipboard.Hotkey")]
    [InlineData("Screenshot.ImageTranslateHotkey")]
    [InlineData("Audio.CycleHotkey")]
    [InlineData("Power.CycleHotkey")]
    [InlineData("UI.ShowPower")]
    [InlineData("UI.ShowAudio")]
    [InlineData("UI.ShowMem")]
    [InlineData("UI.ShowTranslate")]
    [InlineData("UI.ShowLauncher")]
    [InlineData("UI.ShowClipboard")]
    [InlineData("UI.ShowGallery")]
    [InlineData("UI.ShowTemp")]
    [InlineData("AutoStart.Enabled")]
    [InlineData("AutoStart.LastMethod")]
    [InlineData("Screenshot.RootDir")]
    [InlineData("Screenshot.GameBarEnabled")]
    [InlineData("Screenshot.GameBarDir")]
    [InlineData("Screenshot.GameBarHotkey")]
    [InlineData("Translate.From")]
    [InlineData("Translate.To")]
    [InlineData("Temp.IntervalMs")]
    [InlineData("Temp.WarnC")]
    [InlineData("Temp.CriticalC")]
    [InlineData("Perf.ShowChart")]
    [InlineData("AutoCleanEnabled")]
    [InlineData("AutoCleanByTime")]
    [InlineData("AutoCleanByThreshold")]
    [InlineData("AutoCleanMinutes")]
    [InlineData("AutoCleanThreshold")]
    [InlineData("AutoCleanAllowFreezes")]
    [InlineData("Clean.WorkingSet")]
    [InlineData("Clean.SystemFileCache")]
    [InlineData("Clean.ModifiedPageList")]
    [InlineData("Clean.StandbyList")]
    [InlineData("Clean.StandbyListNoPrio")]
    [InlineData("Clean.ModifiedFileCache")]
    [InlineData("Clean.RegistryCache")]
    [InlineData("Clean.CombineMemoryLists")]
    public void LegacyPreferenceKey_RemainsExactlyCompatible(string key) =>
        Assert.Contains(key, PreferenceKeys.LegacyKeys);

    [Fact]
    public void CentralPreferenceKeys_AreUnique() =>
        Assert.Equal(PreferenceKeys.LegacyKeys.Count,
            PreferenceKeys.LegacyKeys.Distinct(StringComparer.Ordinal).Count());
}

public sealed class EntryPointAndTerminologyTests
{
    [Theory]
    [InlineData("ShowWindow", AppCommandId.WindowShow)]
    [InlineData("MemoryClean", AppCommandId.MemoryClean)]
    [InlineData("TextTranslate", AppCommandId.TranslateText)]
    [InlineData("ForegroundScreenshot", AppCommandId.ScreenshotForeground)]
    [InlineData("ClipboardHistory", AppCommandId.ClipboardOpen)]
    [InlineData("AudioCycle", AppCommandId.AudioCycle)]
    [InlineData("PowerCycle", AppCommandId.PowerCycle)]
    [InlineData("Settings", AppCommandId.SettingsOpen)]
    [InlineData("Exit", AppCommandId.AppExit)]
    [InlineData("Update", AppCommandId.UpdateCheck)]
    public void DuplicateEntryPoint_MapsToOneCommand(string action, AppCommandId expected)
    {
        var entries = AppEntryPointCatalog.All.Where(x => x.Action == action).ToArray();
        Assert.True(entries.Length >= 2);
        Assert.All(entries, entry => Assert.Equal(expected, entry.CommandId));
    }

    [Theory]
    [InlineData(AppCommandId.PowerActivate, "电源计划")]
    [InlineData(AppCommandId.AudioActivate, "音频输出")]
    [InlineData(AppCommandId.MemoryClean, "内存清理")]
    [InlineData(AppCommandId.TranslateText, "文本翻译")]
    [InlineData(AppCommandId.TranslateImageRegion, "图片翻译")]
    [InlineData(AppCommandId.ScreenshotForeground, "前台截图")]
    [InlineData(AppCommandId.ClipboardOpen, "剪贴板历史")]
    [InlineData(AppCommandId.LauncherShow, "快捷启动")]
    [InlineData(AppCommandId.MonitorChartOpen, "性能趋势")]
    public void UserFacingTerms_AreCanonical(AppCommandId id, string term) =>
        Assert.Contains(term, AppCommandCatalog.Get(id).Term);
}
