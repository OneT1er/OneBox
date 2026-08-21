using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using System.IO;
using Microsoft.Win32;
using PowerAudioManager.Commands;

namespace PowerAudioManager
{
    public static class AppPrefs
    {
        const string KeyPath = @"Software\PowerAudioManager\App";
        public static bool GetBool(string key, bool defaultValue)
        {
            try { using (var k = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(KeyPath)) {
                if (k == null) return defaultValue;
                var v = k.GetValue(key) as string;
                if (v == "1") return true; if (v == "0") return false;
            } } catch { }
            return defaultValue;
        }
        public static bool SetBool(string key, bool v)
        {
            try
            {
                using (var k = Microsoft.Win32.Registry.CurrentUser.CreateSubKey(KeyPath))
                {
                    if (k == null) return false;
                    k.SetValue(key, v ? "1" : "0");
                    return true;
                }
            }
            catch { return false; }
        }
        public static bool GetDouble(string key, out double value)
        {
            value = 0;
            try { using (var k = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(KeyPath)) {
                if (k == null) return false;
                var v = k.GetValue(key) as string;
                if (v == null) return false;
                return double.TryParse(v, System.Globalization.NumberStyles.Any, System.Globalization.CultureInfo.InvariantCulture, out value);
            } } catch { return false; }
        }
        public static bool SetDouble(string key, double v)
        {
            try
            {
                using (var k = Microsoft.Win32.Registry.CurrentUser.CreateSubKey(KeyPath))
                {
                    if (k == null) return false;
                    k.SetValue(key, v.ToString(System.Globalization.CultureInfo.InvariantCulture));
                    return true;
                }
            }
            catch { return false; }
        }

        public static string GetString(string key, string defaultValue)
        {
            try
            {
                using (var k = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(KeyPath))
                {
                    if (k == null) return defaultValue;
                    var v = k.GetValue(key) as string;
                    return string.IsNullOrEmpty(v) ? defaultValue : v;
                }
            }
            catch { return defaultValue; }
        }

        public static bool SetString(string key, string v)
        {
            try
            {
                using (var k = Microsoft.Win32.Registry.CurrentUser.CreateSubKey(KeyPath))
                {
                    if (k == null) return false;
                    k.SetValue(key, v ?? "");
                    return true;
                }
            }
            catch { return false; }
        }

        public static int GetInt(string key, int defaultValue)
        {
            try
            {
                using (var k = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(KeyPath))
                {
                    if (k == null) return defaultValue;
                    var v = k.GetValue(key) as string;
                    int n; return int.TryParse(v, out n) ? n : defaultValue;
                }
            }
            catch { return defaultValue; }
        }

        public static bool SetInt(string key, int v)
        {
            try
            {
                using (var k = Microsoft.Win32.Registry.CurrentUser.CreateSubKey(KeyPath))
                {
                    if (k == null) return false;
                    k.SetValue(key, v.ToString());
                    return true;
                }
            }
            catch { return false; }
        }

        public static bool Get(PreferenceDefinition<bool> definition) =>
            GetBool(definition.Key, definition.DefaultValue);

        public static int Get(PreferenceDefinition<int> definition) =>
            GetInt(definition.Key, definition.DefaultValue);

        public static string Get(PreferenceDefinition<string> definition) =>
            GetString(definition.Key, definition.DefaultValue);

        public static double Get(PreferenceDefinition<double> definition)
        {
            return GetDouble(definition.Key, out var value) ? value : definition.DefaultValue;
        }

        public static bool Set(PreferenceDefinition<bool> definition, bool value) => SetBool(definition.Key, value);
        public static bool Set(PreferenceDefinition<int> definition, int value) => SetInt(definition.Key, value);
        public static bool Set(PreferenceDefinition<string> definition, string value) => SetString(definition.Key, value);
        public static bool Set(PreferenceDefinition<double> definition, double value) => SetDouble(definition.Key, value);
    }

    public static class DevicePrefs
    {
        const string KeyPath = @"Software\PowerAudioManager\Devices";

        public static bool IsHidden(string deviceName)
        {
            try
            {
                using (var k = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(KeyPath))
                {
                    if (k == null) return false;
                    var v = k.GetValue(deviceName + ":hidden") as string;
                    return v == "1";
                }
            }
            catch { return false; }
        }

        public static void SetHidden(string deviceName, bool hidden)
        {
            try
            {
                using (var k = Microsoft.Win32.Registry.CurrentUser.CreateSubKey(KeyPath))
                {
                    if (hidden) k.SetValue(deviceName + ":hidden", "1");
                    else k.DeleteValue(deviceName + ":hidden", false);
                }
            }
            catch { }
        }

        public static int GetHotkey(string deviceName)
        {
            try
            {
                using (var k = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(KeyPath))
                {
                    if (k == null) return 0;
                    var v = k.GetValue(deviceName + ":hotkey") as string;
                    int n; return int.TryParse(v, out n) ? n : 0;
                }
            }
            catch { return 0; }
        }

        public static void SetHotkeyKey(string deviceName, int encoded)
        {
            try
            {
                using (var k = Microsoft.Win32.Registry.CurrentUser.CreateSubKey(KeyPath))
                {
                    if (encoded != 0)
                    {
                        // 清除冲突（任何其他设备使用了相同的编码值）
                        var s = encoded.ToString();
                        foreach (var existing in k.GetValueNames())
                        {
                            if (!existing.EndsWith(":hotkey")) continue;
                            if (existing == deviceName + ":hotkey") continue;
                            if ((k.GetValue(existing) as string) == s)
                                k.DeleteValue(existing, false);
                        }
                        k.SetValue(deviceName + ":hotkey", s);
                    }
                    else k.DeleteValue(deviceName + ":hotkey", false);
                }
            }
            catch { }
        }

        public static List<KeyValuePair<string, int>> GetAllHotkeys()
        {
            var list = new List<KeyValuePair<string, int>>();
            try
            {
                using (var k = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(KeyPath))
                {
                    if (k == null) return list;
                    foreach (var n in k.GetValueNames())
                    {
                        if (!n.EndsWith(":hotkey")) continue;
                        var v = k.GetValue(n) as string;
                        int digit;
                        if (int.TryParse(v, out digit) && digit != 0)
                        {
                            var name = n.Substring(0, n.Length - ":hotkey".Length);
                            list.Add(new KeyValuePair<string, int>(name, digit));
                        }
                    }
                }
            }
            catch { }
            return list;
        }
    }

}
