using System;
using System.Collections.Generic;

namespace PowerAudioManager.Commands
{
    public sealed record AppCommandDefinition(AppCommandId Id, string Term, Type PayloadType = null,
        bool PreventReentry = false);

    public static class AppCommandCatalog
    {
        static readonly AppCommandDefinition[] _all =
        {
            D(AppCommandId.WindowShow, "显示窗口"),
            D(AppCommandId.WindowHide, "隐藏窗口"),
            D<WindowCollapsedPayload>(AppCommandId.WindowSetCollapsed, "折叠窗口"),
            D(AppCommandId.AppExit, "退出 OneBox", busy: true),
            D<SettingsOpenPayload>(AppCommandId.SettingsOpen, "设置"),
            D(AppCommandId.PowerList, "电源计划列表"),
            D<PowerActivatePayload>(AppCommandId.PowerActivate, "切换电源计划"),
            D(AppCommandId.PowerCycle, "循环切换电源计划"),
            D(AppCommandId.AudioList, "音频输出列表"),
            D<AudioActivatePayload>(AppCommandId.AudioActivate, "切换音频输出"),
            D(AppCommandId.AudioCycle, "循环切换音频输出"),
            D<AudioVolumePayload>(AppCommandId.AudioSetVolume, "设置音量"),
            D<AudioMutePayload>(AppCommandId.AudioSetMute, "设置静音"),
            D<MemoryCleanPayload>(AppCommandId.MemoryClean, "内存清理", true),
            D<TextTranslatePayload>(AppCommandId.TranslateText, "文本翻译", true),
            D(AppCommandId.TranslateImageRegion, "图片翻译", busy: true),
            D(AppCommandId.TranslateImageClipboard, "图片翻译", busy: true),
            D(AppCommandId.ScreenshotForeground, "前台截图", busy: true),
            D(AppCommandId.ScreenshotOpenGallery, "截图文件夹"),
            D<ClipboardOpenPayload>(AppCommandId.ClipboardOpen, "剪贴板历史"),
            D(AppCommandId.ClipboardClear, "清空剪贴板历史"),
            D(AppCommandId.LauncherShow, "快捷启动"),
            D<LauncherAddPayload>(AppCommandId.LauncherAdd, "添加快捷启动项"),
            D<LauncherRemovePayload>(AppCommandId.LauncherRemove, "移除快捷启动项"),
            D<LauncherLaunchPayload>(AppCommandId.LauncherLaunch, "运行快捷启动项"),
            D(AppCommandId.MonitorChartOpen, "性能趋势"),
            D(AppCommandId.MonitorStart, "启动性能监控"),
            D(AppCommandId.MonitorStop, "停止性能监控"),
            D<UpdateCheckPayload>(AppCommandId.UpdateCheck, "检查更新"),
            D<AutoStartApplyPayload>(AppCommandId.AutoStartApply, "开机自启"),
            D(AppCommandId.RuntimeRefreshHotkeys, "刷新全局快捷键"),
            D(AppCommandId.RuntimeRestartAutoClean, "刷新自动内存清理"),
            D<GeneralRuntimePayload>(AppCommandId.RuntimeApplyGeneral, "应用常规设置"),
            D(AppCommandId.RuntimeRebuildModules, "应用模块设置")
        };

        static readonly Dictionary<AppCommandId, AppCommandDefinition> _byId = BuildIndex();
        public static IReadOnlyList<AppCommandDefinition> All => _all;

        public static bool TryGet(AppCommandId id, out AppCommandDefinition definition) =>
            _byId.TryGetValue(id, out definition);

        public static AppCommandDefinition Get(AppCommandId id) =>
            _byId.TryGetValue(id, out var definition)
                ? definition
                : throw new ArgumentOutOfRangeException(nameof(id), id, "未登记的应用命令");

        static AppCommandDefinition D(AppCommandId id, string term, bool busy = false) =>
            new AppCommandDefinition(id, term, null, busy);

        static AppCommandDefinition D<T>(AppCommandId id, string term, bool busy = false) =>
            new AppCommandDefinition(id, term, typeof(T), busy);

        static Dictionary<AppCommandId, AppCommandDefinition> BuildIndex()
        {
            var result = new Dictionary<AppCommandId, AppCommandDefinition>();
            foreach (var definition in _all)
                if (!result.TryAdd(definition.Id, definition))
                    throw new InvalidOperationException("重复的应用命令定义: " + definition.Id);
            return result;
        }
    }
}
