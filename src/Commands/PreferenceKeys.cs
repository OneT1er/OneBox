using System;
using System.Collections.Generic;

namespace PowerAudioManager.Commands
{
    public sealed record PreferenceDefinition<T>(string Key, T DefaultValue);

    public static class PreferenceKeys
    {
        public static class Hotkeys
        {
            public static readonly PreferenceDefinition<int> Screenshot = new("Screenshot.Hotkey", 0);
            public static readonly PreferenceDefinition<int> Clipboard = new("Clipboard.Hotkey", 0);
            public static readonly PreferenceDefinition<int> ImageTranslate = new("Screenshot.ImageTranslateHotkey", 0);
            public static readonly PreferenceDefinition<int> AudioCycle = new("Audio.CycleHotkey", 0);
            public static readonly PreferenceDefinition<int> PowerCycle = new("Power.CycleHotkey", 0);
        }

        public static class Modules
        {
            public static readonly PreferenceDefinition<bool> Power = new("UI.ShowPower", true);
            public static readonly PreferenceDefinition<bool> Audio = new("UI.ShowAudio", true);
            public static readonly PreferenceDefinition<bool> Memory = new("UI.ShowMem", true);
            public static readonly PreferenceDefinition<bool> Translate = new("UI.ShowTranslate", true);
            public static readonly PreferenceDefinition<bool> Launcher = new("UI.ShowLauncher", true);
            public static readonly PreferenceDefinition<bool> Clipboard = new("UI.ShowClipboard", true);
            public static readonly PreferenceDefinition<bool> Gallery = new("UI.ShowGallery", true);
            public static readonly PreferenceDefinition<bool> Monitor = new("UI.ShowTemp", true);
        }

        public static class AutoStart
        {
            public static readonly PreferenceDefinition<bool> Enabled = new("AutoStart.Enabled", true);
            public static readonly PreferenceDefinition<int> LastMethod = new("AutoStart.LastMethod", 0);
        }

        public static class Screenshot
        {
            public static readonly PreferenceDefinition<string> RootDirectory = new("Screenshot.RootDir", "");
            public static readonly PreferenceDefinition<bool> GameBarEnabled = new("Screenshot.GameBarEnabled", false);
            public static readonly PreferenceDefinition<string> GameBarDirectory = new("Screenshot.GameBarDir", "");
            public static readonly PreferenceDefinition<int> GameBarHotkey = new("Screenshot.GameBarHotkey", 0);
            public static readonly PreferenceDefinition<bool> ExternalTakeoverEnabled = new("Screenshot.ExternalTakeoverEnabled", false);
            public static readonly PreferenceDefinition<string> ExternalTakeoverDirectory = new("Screenshot.ExternalTakeoverDir", "");
        }

        public static class Translate
        {
            public static readonly PreferenceDefinition<string> From = new("Translate.From", "auto");
            public static readonly PreferenceDefinition<string> To = new("Translate.To", "zh");
            public const string AppId = "AppId";
            public const string ApiKey = "Key";
            public const string Instruction = "Instruction";
        }

        public static class Monitor
        {
            public static readonly PreferenceDefinition<int> IntervalMs = new("Temp.IntervalMs", 1000);
            public static readonly PreferenceDefinition<int> WarningC = new("Temp.WarnC", 80);
            public static readonly PreferenceDefinition<int> CriticalC = new("Temp.CriticalC", 95);
            public static readonly PreferenceDefinition<bool> ShowChart = new("Perf.ShowChart", true);
        }

        public static class Memory
        {
            public static readonly PreferenceDefinition<bool> AutoEnabled = new("AutoCleanEnabled", false);
            public static readonly PreferenceDefinition<bool> AutoByTime = new("AutoCleanByTime", true);
            public static readonly PreferenceDefinition<bool> AutoByThreshold = new("AutoCleanByThreshold", true);
            public static readonly PreferenceDefinition<double> AutoMinutes = new("AutoCleanMinutes", 30);
            public static readonly PreferenceDefinition<double> AutoThreshold = new("AutoCleanThreshold", 80);
            public static readonly PreferenceDefinition<bool> AllowFreezes = new("AutoCleanAllowFreezes", false);
            public static readonly PreferenceDefinition<bool> WorkingSet = new("Clean.WorkingSet", true);
            public static readonly PreferenceDefinition<bool> SystemFileCache = new("Clean.SystemFileCache", true);
            public static readonly PreferenceDefinition<bool> ModifiedPageList = new("Clean.ModifiedPageList", false);
            public static readonly PreferenceDefinition<bool> StandbyList = new("Clean.StandbyList", false);
            public static readonly PreferenceDefinition<bool> StandbyListNoPriority = new("Clean.StandbyListNoPrio", true);
            public static readonly PreferenceDefinition<bool> ModifiedFileCache = new("Clean.ModifiedFileCache", true);
            public static readonly PreferenceDefinition<bool> RegistryCache = new("Clean.RegistryCache", true);
            public static readonly PreferenceDefinition<bool> CombineMemoryLists = new("Clean.CombineMemoryLists", true);
        }

        public static class Window
        {
            public static readonly PreferenceDefinition<bool> Topmost = new("Topmost", false);
            public static readonly PreferenceDefinition<bool> LockPosition = new("LockPosition", false);
            public static readonly PreferenceDefinition<bool> AutoCollapse = new("AutoCollapse", true);
            public static readonly PreferenceDefinition<int> AutoCollapseDelay = new("AutoCollapseDelay", 8);
        }

        public static IReadOnlyList<string> LegacyKeys { get; } = BuildLegacyKeys();

        static IReadOnlyList<string> BuildLegacyKeys()
        {
            var keys = new List<string>();
            foreach (var group in typeof(PreferenceKeys).GetNestedTypes())
                foreach (var field in group.GetFields(System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static))
                    if (field.GetValue(null) is object value)
                    {
                        var keyProperty = value.GetType().GetProperty(nameof(PreferenceDefinition<int>.Key));
                        if (keyProperty?.GetValue(value) is string key) keys.Add(key);
                    }
            return keys.AsReadOnly();
        }
    }
}
