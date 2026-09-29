using System;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using PowerAudioManager.AudioStudio;
using PowerAudioManager.Commands;

namespace PowerAudioManager
{
    // 所有固定/可配置全局热键由 HotkeyDefinitions 单表登记；此处只负责原生注册和命令分发。
    public partial class MainWindow : Window
    {
        IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
        {
            if (msg != Native.WM_HOTKEY) return IntPtr.Zero;
            int nativeId = wParam.ToInt32();
            if (nativeId == StudioSoundpadHotkeys.StopNativeId)
            {
                StudioController.Instance.StopSound();
                handled = true;
                return IntPtr.Zero;
            }
            if (nativeId >= StudioSoundpadHotkeys.NativeIdBase &&
                nativeId < StudioSoundpadHotkeys.NativeIdBase + StudioSoundpadHotkeys.MaxEffects)
            {
                int slot = nativeId - StudioSoundpadHotkeys.NativeIdBase;
                var effects = StudioController.Instance.Settings.SoundEffects;
                if (slot < effects.Count) _ = PlaySoundpadHotkeyAsync(effects[slot].Id);
                handled = true;
                return IntPtr.Zero;
            }
            var definition = HotkeyDefinitions.All.FirstOrDefault(item => item.NativeId == nativeId);
            if (definition != null)
            {
                AppLog.Log("Hotkey", definition.CommandId + " triggered");
                _ = ExecuteCommandAsync(definition.CommandId, CommandSource.Hotkey,
                    CreateHotkeyPayload(definition.CommandId));
                handled = true;
                return IntPtr.Zero;
            }

            if (_hotkeyMap.TryGetValue(nativeId, out string deviceId))
            {
                _ = ExecuteCommandAsync(AppCommandId.AudioActivate, CommandSource.Hotkey,
                    new AudioActivatePayload(deviceId));
                handled = true;
            }
            return IntPtr.Zero;
        }

        async Task PlaySoundpadHotkeyAsync(string effectId)
        {
            try { await StudioController.Instance.PlaySoundAsync(effectId); }
            catch (Exception ex) { AppLog.Log("OneMic soundpad hotkey", ex); }
        }

        object CreateHotkeyPayload(AppCommandId commandId)
        {
            switch (commandId)
            {
                case AppCommandId.TranslateText:
                    try { return new TextTranslatePayload(Clipboard.ContainsText() ? Clipboard.GetText() : null); }
                    catch { return new TextTranslatePayload(null); }
                case AppCommandId.ClipboardOpen:
                    Native.GetCursorPos(out var point);
                    return new ClipboardOpenPayload(point.X, point.Y);
                default:
                    return null;
            }
        }

        // 兼容旧的内部调用点；业务仍统一由 dispatcher 执行。
        void CycleAudioDevice() =>
            _ = ExecuteCommandAsync(AppCommandId.AudioCycle, CommandSource.Hotkey);

        void CyclePowerPlan() =>
            _ = ExecuteCommandAsync(AppCommandId.PowerCycle, CommandSource.Hotkey);

        void UnregisterAllHotkeys()
        {
            if (_hotkeyHwnd == IntPtr.Zero) return;
            foreach (var id in _hotkeyMap.Keys) Native.UnregisterHotKey(_hotkeyHwnd, id);
            _hotkeyMap.Clear();
            foreach (var definition in HotkeyDefinitions.All)
                Native.UnregisterHotKey(_hotkeyHwnd, definition.NativeId);
            Native.UnregisterHotKey(_hotkeyHwnd, StudioSoundpadHotkeys.StopNativeId);
            for (int slot = 0; slot < StudioSoundpadHotkeys.MaxEffects; slot++)
                Native.UnregisterHotKey(_hotkeyHwnd, StudioSoundpadHotkeys.NativeIdBase + slot);
        }

        internal void PauseHotkeys() => UnregisterAllHotkeys();

        internal bool TestHotkey(int encoded)
        {
            if (_hotkeyHwnd == IntPtr.Zero || encoded == 0) return true;
            HotkeyDefinitions.Decode(encoded, out uint modifiers, out uint virtualKey);
            const int testId = 0xBE00;
            Native.UnregisterHotKey(_hotkeyHwnd, testId);
            bool ok = Native.RegisterHotKey(_hotkeyHwnd, testId, modifiers, virtualKey);
            Native.UnregisterHotKey(_hotkeyHwnd, testId);
            return ok;
        }

        internal void RefreshHotkeys()
        {
            if (_hotkeyHwnd == IntPtr.Zero) return;
            UnregisterAllHotkeys();
            foreach (var definition in HotkeyDefinitions.All)
            {
                if (!definition.Enabled()) continue;
                int encoded = HotkeyDefinitions.ResolveEncoded(definition);
                if (encoded == 0) continue;
                HotkeyDefinitions.Decode(encoded, out uint modifiers, out uint virtualKey);
                if (definition.PreferenceKey?.StartsWith("AudioStudio.", StringComparison.Ordinal) == true) modifiers |= 0x4000; // MOD_NOREPEAT
                if (!Native.RegisterHotKey(_hotkeyHwnd, definition.NativeId, modifiers, virtualKey))
                    AppLog.Log("Hotkey", "register failed: " + definition.CommandId);
            }

            int nextId = Native.HOTKEY_ID_BASE;
            foreach (var pair in DevicePrefs.GetAllHotkeys())
            {
                if (pair.Value == 0) continue;
                HotkeyDefinitions.Decode(pair.Value, out uint modifiers, out uint virtualKey);
                int nativeId = nextId++;
                if (Native.RegisterHotKey(_hotkeyHwnd, nativeId, modifiers, virtualKey))
                    _hotkeyMap[nativeId] = pair.Key;
            }
            var effects = StudioController.Instance.Settings.SoundEffects;
            for (int slot = 0; slot < effects.Count; slot++)
            {
                int encoded = effects[slot].Hotkey;
                if (encoded == 0) continue;
                HotkeyDefinitions.Decode(encoded, out uint modifiers, out uint key);
                if (!Native.RegisterHotKey(_hotkeyHwnd, StudioSoundpadHotkeys.NativeIdBase + slot,
                    modifiers | 0x4000, key))
                    AppLog.Log("Hotkey", "OneMic soundpad shortcut unavailable: " + effects[slot].Path);
            }
            if (effects.Count > 0 && !Native.RegisterHotKey(_hotkeyHwnd, StudioSoundpadHotkeys.StopNativeId,
                Native.MOD_CONTROL | Native.MOD_SHIFT | 0x4000, 0x30))
                AppLog.Log("Hotkey", "OneMic soundpad stop unavailable");
        }
    }
}
