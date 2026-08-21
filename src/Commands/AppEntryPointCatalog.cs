using System.Collections.Generic;

namespace PowerAudioManager.Commands
{
    public sealed record AppEntryPoint(string Action, CommandSource Source, AppCommandId CommandId);

    // 描述重复用户入口应汇聚到的命令，既作为审计清单，也供入口一致性测试使用。
    public static class AppEntryPointCatalog
    {
        static readonly AppEntryPoint[] _all =
        {
            new("ShowWindow", CommandSource.MainWindow, AppCommandId.WindowShow),
            new("ShowWindow", CommandSource.Tray, AppCommandId.WindowShow),
            new("HideWindow", CommandSource.MainWindow, AppCommandId.WindowHide),
            new("MemoryClean", CommandSource.MainWindow, AppCommandId.MemoryClean),
            new("MemoryClean", CommandSource.Tray, AppCommandId.MemoryClean),
            new("MemoryClean", CommandSource.System, AppCommandId.MemoryClean),
            new("TextTranslate", CommandSource.MainWindow, AppCommandId.TranslateText),
            new("TextTranslate", CommandSource.Hotkey, AppCommandId.TranslateText),
            new("ImageTranslate", CommandSource.MainWindow, AppCommandId.TranslateImageClipboard),
            new("ImageTranslate", CommandSource.Hotkey, AppCommandId.TranslateImageRegion),
            new("ForegroundScreenshot", CommandSource.MainWindow, AppCommandId.ScreenshotForeground),
            new("ForegroundScreenshot", CommandSource.Hotkey, AppCommandId.ScreenshotForeground),
            new("ClipboardHistory", CommandSource.MainWindow, AppCommandId.ClipboardOpen),
            new("ClipboardHistory", CommandSource.Hotkey, AppCommandId.ClipboardOpen),
            new("AudioCycle", CommandSource.MainWindow, AppCommandId.AudioCycle),
            new("AudioCycle", CommandSource.Hotkey, AppCommandId.AudioCycle),
            new("PowerCycle", CommandSource.MainWindow, AppCommandId.PowerCycle),
            new("PowerCycle", CommandSource.Hotkey, AppCommandId.PowerCycle),
            new("Settings", CommandSource.MainWindow, AppCommandId.SettingsOpen),
            new("Settings", CommandSource.Tray, AppCommandId.SettingsOpen),
            new("Exit", CommandSource.MainWindow, AppCommandId.AppExit),
            new("Exit", CommandSource.Tray, AppCommandId.AppExit),
            new("Update", CommandSource.Settings, AppCommandId.UpdateCheck),
            new("Update", CommandSource.Tray, AppCommandId.UpdateCheck)
        };

        public static IReadOnlyList<AppEntryPoint> All => _all;
    }
}
