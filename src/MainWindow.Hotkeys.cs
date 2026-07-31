using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using System.IO;
using System.Runtime.InteropServices;

namespace PowerAudioManager
{
    // 全局快捷键：WndProc 分发 + 注册/注销（翻译/截图/剪贴板/图片翻译/循环切换/设备热键）。
    public partial class MainWindow : Window
    {
        IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
        {
            if (msg == Native.WM_HOTKEY)
            {
                int id = wParam.ToInt32();
                if (id == Native.HOTKEY_ID_TRANSLATE)
                {
                    AppLog.Log("Hotkey", "translate (Ctrl+Shift+T) triggered");
                    TranslateFromClipboard();
                    handled = true;
                    return IntPtr.Zero;
                }
                if (id == Native.HOTKEY_ID_SCREENSHOT)
                {
                    AppLog.Log("Hotkey", "screenshot triggered");
                    // 线程池执行截图避免阻塞热键循环，Toast 内部会回到 UI 线程。
                    System.Threading.ThreadPool.QueueUserWorkItem(_ => ScreenshotService.CaptureForeground());
                    handled = true;
                    return IntPtr.Zero;
                }
                if (id == Native.HOTKEY_ID_CLIPBOARD)
                {
                    AppLog.Log("Hotkey", "clipboard history triggered");
                    Native.POINT pt;
                    Native.GetCursorPos(out pt);
                    ClipboardHistoryPanel.ShowAt(this, pt.X, pt.Y);
                    handled = true;
                    return IntPtr.Zero;
                }
                if (id == Native.HOTKEY_ID_IMAGE_TRANSLATE)
                {
                    AppLog.Log("Hotkey", "image translate (region capture) triggered");
                    HandleImageTranslateHotkey();
                    handled = true;
                    return IntPtr.Zero;
                }
                if (id == Native.HOTKEY_ID_AUDIO_CYCLE)
                {
                    AppLog.Log("Hotkey", "audio cycle triggered");
                    CycleAudioDevice();
                    handled = true;
                    return IntPtr.Zero;
                }
                if (id == Native.HOTKEY_ID_POWER_CYCLE)
                {
                    AppLog.Log("Hotkey", "power cycle triggered");
                    CyclePowerPlan();
                    handled = true;
                    return IntPtr.Zero;
                }
                string devName;
                if (_hotkeyMap.TryGetValue(id, out devName))
                {
                    AppLog.Log("Hotkey", "switch audio device: " + devName);
                    if (AudioDevices.SetDefaultDevice(devName))
                    {
                        _currentDeviceId = null;
                        VolumeControl.Invalidate();
                        LoadData();
                        ScheduleVolumeRefresh();
                    }
                    handled = true;
                }
            }
            return IntPtr.Zero;
        }

        void CycleAudioDevice()
        {
            try
            {
                var visible = AudioDevices.GetOutputDevices().FindAll(d => !d.IsHidden);
                if (visible.Count == 0) return;
                int cur = visible.FindIndex(d => d.IsDefault);
                int next = cur < 0 ? 0 : (cur + 1) % visible.Count;
                var target = visible[next];
                if (AudioDevices.SetDefaultDevice(target.Id))
                {
                    _currentDeviceId = null;
                    VolumeControl.Invalidate();
                    LoadData();
                    ScheduleVolumeRefresh();
                    AppProfileToast.ShowAudioSwitch(target.Name);
                    AppLog.Log("Hotkey", "cycle audio -> " + target.Name);
                }
            }
            catch (Exception ex) { AppLog.Log("CycleAudio", ex); }
        }

        void CyclePowerPlan()
        {
            System.Threading.ThreadPool.QueueUserWorkItem(_ =>
            {
                try
                {
                    var plans = PowerPlanService.GetPowerPlans();
                    if (plans.Count == 0) return;
                    int cur = plans.FindIndex(p => p.IsActive);
                    int next = cur < 0 ? 0 : (cur + 1) % plans.Count;
                    var target = plans[next];
                    bool ok = PowerPlanService.SetActivePlan(target.Guid);
                    if (ok)
                    {
                        AppLog.Log("Hotkey", "cycle power -> " + target.Name + " (" + target.Guid + ")");
                        Dispatcher.BeginInvoke(new Action(() =>
                        {
                            try { LoadData(); } catch { }
                            AppProfileToast.ShowPowerSwitch(target.Name);
                        }));
                    }
                }
                catch (Exception ex) { AppLog.Log("CyclePower", ex); }
            });
        }

        void UnregisterAllHotkeys()
        {
            if (_hotkeyHwnd == IntPtr.Zero) return;
            foreach (var id in _hotkeyMap.Keys) Native.UnregisterHotKey(_hotkeyHwnd, id);
            _hotkeyMap.Clear();
            Native.UnregisterHotKey(_hotkeyHwnd, Native.HOTKEY_ID_TRANSLATE);
            Native.UnregisterHotKey(_hotkeyHwnd, Native.HOTKEY_ID_SCREENSHOT);
            Native.UnregisterHotKey(_hotkeyHwnd, Native.HOTKEY_ID_CLIPBOARD);
            Native.UnregisterHotKey(_hotkeyHwnd, Native.HOTKEY_ID_IMAGE_TRANSLATE);
            Native.UnregisterHotKey(_hotkeyHwnd, Native.HOTKEY_ID_AUDIO_CYCLE);
            Native.UnregisterHotKey(_hotkeyHwnd, Native.HOTKEY_ID_POWER_CYCLE);
        }

        internal bool TestHotkey(int encoded)
        {
            if (_hotkeyHwnd == IntPtr.Zero) return true;
            if (encoded == 0) return true;
            int mods = (encoded >> 16) & 0xFFFF;
            uint vk = (uint)(encoded & 0xFFFF);
            uint winMods = 0;
            if ((mods & 1) != 0) winMods |= Native.MOD_ALT;
            if ((mods & 2) != 0) winMods |= Native.MOD_CONTROL;
            if ((mods & 4) != 0) winMods |= Native.MOD_SHIFT;
            if ((mods & 8) != 0) winMods |= Native.MOD_WIN;
            int testId = 0xBE00; // 临时测试 ID，实际热键不会用
            Native.UnregisterHotKey(_hotkeyHwnd, testId);
            bool ok = Native.RegisterHotKey(_hotkeyHwnd, testId, winMods, vk);
            Native.UnregisterHotKey(_hotkeyHwnd, testId);
            return ok;
        }

        internal void RefreshHotkeys()
        {
            if (_hotkeyHwnd == IntPtr.Zero) return;
            foreach (var id in _hotkeyMap.Keys) Native.UnregisterHotKey(_hotkeyHwnd, id);
            _hotkeyMap.Clear();
            // 翻译快捷键：固定 Ctrl+Shift+T
            Native.UnregisterHotKey(_hotkeyHwnd, Native.HOTKEY_ID_TRANSLATE);
            Native.RegisterHotKey(_hotkeyHwnd, Native.HOTKEY_ID_TRANSLATE, Native.MOD_CONTROL | Native.MOD_SHIFT, 0x54);
            // 截图快捷键：用户自定义（Screenshot.Hotkey，编码同设备热键：hi16=修饰键, lo16=VK）
            Native.UnregisterHotKey(_hotkeyHwnd, Native.HOTKEY_ID_SCREENSHOT);
            int shotEncoded = AppPrefs.GetInt("Screenshot.Hotkey", 0);
            if (shotEncoded != 0)
            {
                int smods = (shotEncoded >> 16) & 0xFFFF;
                uint svk = (uint)(shotEncoded & 0xFFFF);
                uint swinMods = 0;
                if ((smods & 1) != 0) swinMods |= Native.MOD_ALT;
                if ((smods & 2) != 0) swinMods |= Native.MOD_CONTROL;
                if ((smods & 4) != 0) swinMods |= Native.MOD_SHIFT;
                if ((smods & 8) != 0) swinMods |= Native.MOD_WIN;
                Native.RegisterHotKey(_hotkeyHwnd, Native.HOTKEY_ID_SCREENSHOT, swinMods, svk);
            }
            // 剪贴板历史快捷键：用户自定义（Clipboard.Hotkey）
            Native.UnregisterHotKey(_hotkeyHwnd, Native.HOTKEY_ID_CLIPBOARD);
            int clipEncoded = AppPrefs.GetInt("Clipboard.Hotkey", 0);
            if (clipEncoded != 0)
            {
                int cmods = (clipEncoded >> 16) & 0xFFFF;
                uint cvk = (uint)(clipEncoded & 0xFFFF);
                uint cwinMods = 0;
                if ((cmods & 1) != 0) cwinMods |= Native.MOD_ALT;
                if ((cmods & 2) != 0) cwinMods |= Native.MOD_CONTROL;
                if ((cmods & 4) != 0) cwinMods |= Native.MOD_SHIFT;
                if ((cmods & 8) != 0) cwinMods |= Native.MOD_WIN;
                Native.RegisterHotKey(_hotkeyHwnd, Native.HOTKEY_ID_CLIPBOARD, cwinMods, cvk);
            }
            // 图片翻译快捷键：用户自定义（Screenshot.ImageTranslateHotkey）
            Native.UnregisterHotKey(_hotkeyHwnd, Native.HOTKEY_ID_IMAGE_TRANSLATE);
            int itEnc = AppPrefs.GetInt("Screenshot.ImageTranslateHotkey", 0);
            if (itEnc != 0)
            {
                int imods = (itEnc >> 16) & 0xFFFF;
                uint ivk = (uint)(itEnc & 0xFFFF);
                uint iwinMods = 0;
                if ((imods & 1) != 0) iwinMods |= Native.MOD_ALT;
                if ((imods & 2) != 0) iwinMods |= Native.MOD_CONTROL;
                if ((imods & 4) != 0) iwinMods |= Native.MOD_SHIFT;
                if ((imods & 8) != 0) iwinMods |= Native.MOD_WIN;
                Native.RegisterHotKey(_hotkeyHwnd, Native.HOTKEY_ID_IMAGE_TRANSLATE, iwinMods, ivk);
            }
            // 音频循环切换快捷键：用户自定义（Audio.CycleHotkey）-> 切到下一个可见输出设备 + 弹提示
            Native.UnregisterHotKey(_hotkeyHwnd, Native.HOTKEY_ID_AUDIO_CYCLE);
            int cycEnc = AppPrefs.GetInt("Audio.CycleHotkey", 0);
            if (cycEnc != 0)
            {
                int ymods = (cycEnc >> 16) & 0xFFFF;
                uint yvk = (uint)(cycEnc & 0xFFFF);
                uint ywinMods = 0;
                if ((ymods & 1) != 0) ywinMods |= Native.MOD_ALT;
                if ((ymods & 2) != 0) ywinMods |= Native.MOD_CONTROL;
                if ((ymods & 4) != 0) ywinMods |= Native.MOD_SHIFT;
                if ((ymods & 8) != 0) ywinMods |= Native.MOD_WIN;
                Native.RegisterHotKey(_hotkeyHwnd, Native.HOTKEY_ID_AUDIO_CYCLE, ywinMods, yvk);
            }
            // 电源循环切换快捷键：用户自定义（Power.CycleHotkey）-> 切到下一个电源计划 + 弹提示
            Native.UnregisterHotKey(_hotkeyHwnd, Native.HOTKEY_ID_POWER_CYCLE);
            int pwEnc = AppPrefs.GetInt("Power.CycleHotkey", 0);
            if (pwEnc != 0)
            {
                int pmods = (pwEnc >> 16) & 0xFFFF;
                uint pvk = (uint)(pwEnc & 0xFFFF);
                uint pwinMods = 0;
                if ((pmods & 1) != 0) pwinMods |= Native.MOD_ALT;
                if ((pmods & 2) != 0) pwinMods |= Native.MOD_CONTROL;
                if ((pmods & 4) != 0) pwinMods |= Native.MOD_SHIFT;
                if ((pmods & 8) != 0) pwinMods |= Native.MOD_WIN;
                Native.RegisterHotKey(_hotkeyHwnd, Native.HOTKEY_ID_POWER_CYCLE, pwinMods, pvk);
            }
            int nextId = Native.HOTKEY_ID_BASE;
            foreach (var kv in DevicePrefs.GetAllHotkeys())
            {
                int encoded = kv.Value;
                if (encoded == 0) continue;
                int mods = (encoded >> 16) & 0xFFFF;
                uint vk = (uint)(encoded & 0xFFFF);
                uint winMods = 0;
                if ((mods & 1) != 0) winMods |= Native.MOD_ALT;
                if ((mods & 2) != 0) winMods |= Native.MOD_CONTROL;
                if ((mods & 4) != 0) winMods |= Native.MOD_SHIFT;
                if ((mods & 8) != 0) winMods |= Native.MOD_WIN;
                int id = nextId++;
                if (Native.RegisterHotKey(_hotkeyHwnd, id, winMods, vk))
                    _hotkeyMap[id] = kv.Key;
            }
        }
    }
}

