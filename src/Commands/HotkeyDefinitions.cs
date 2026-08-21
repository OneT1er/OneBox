using System;
using System.Collections.Generic;

namespace PowerAudioManager.Commands
{
    public sealed record HotkeyDefinition(AppCommandId CommandId, string PreferenceKey, int NativeId,
        int DefaultEncoded, Func<bool> Enabled);

    public static class HotkeyDefinitions
    {
        public const int FixedTranslateEncoded = ((2 | 4) << 16) | 0x54; // Ctrl+Shift+T

        static readonly HotkeyDefinition[] _all =
        {
            new(AppCommandId.TranslateText, null, Native.HOTKEY_ID_TRANSLATE,
                FixedTranslateEncoded, () => true),
            new(AppCommandId.ScreenshotForeground, PreferenceKeys.Hotkeys.Screenshot.Key,
                Native.HOTKEY_ID_SCREENSHOT, 0, () => true),
            new(AppCommandId.ClipboardOpen, PreferenceKeys.Hotkeys.Clipboard.Key,
                Native.HOTKEY_ID_CLIPBOARD, 0, () => true),
            new(AppCommandId.TranslateImageRegion, PreferenceKeys.Hotkeys.ImageTranslate.Key,
                Native.HOTKEY_ID_IMAGE_TRANSLATE, 0, () => true),
            new(AppCommandId.AudioCycle, PreferenceKeys.Hotkeys.AudioCycle.Key,
                Native.HOTKEY_ID_AUDIO_CYCLE, 0, () => true),
            new(AppCommandId.PowerCycle, PreferenceKeys.Hotkeys.PowerCycle.Key,
                Native.HOTKEY_ID_POWER_CYCLE, 0, () => true)
        };

        public static IReadOnlyList<HotkeyDefinition> All => _all;

        public static int ResolveEncoded(HotkeyDefinition definition) =>
            definition.PreferenceKey == null
                ? definition.DefaultEncoded
                : AppPrefs.GetInt(definition.PreferenceKey, definition.DefaultEncoded);

        public static void Decode(int encoded, out uint modifiers, out uint virtualKey)
        {
            int encodedModifiers = (encoded >> 16) & 0xFFFF;
            modifiers = 0;
            if ((encodedModifiers & 1) != 0) modifiers |= Native.MOD_ALT;
            if ((encodedModifiers & 2) != 0) modifiers |= Native.MOD_CONTROL;
            if ((encodedModifiers & 4) != 0) modifiers |= Native.MOD_SHIFT;
            if ((encodedModifiers & 8) != 0) modifiers |= Native.MOD_WIN;
            virtualKey = (uint)(encoded & 0xFFFF);
        }
    }
}
