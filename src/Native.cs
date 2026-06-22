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

namespace PowerAudioManager
{
    internal static class Native
    {
        [DllImport("user32.dll")]
        public static extern int SetWindowLong(IntPtr hwnd, int index, int newStyle);
        [DllImport("user32.dll")]
        public static extern int GetWindowLong(IntPtr hwnd, int index);
        [DllImport("user32.dll")]
        public static extern bool SetWindowPos(IntPtr hWnd, IntPtr hWndInsertAfter, int X, int Y, int cx, int cy, uint uFlags);
        [DllImport("dwmapi.dll")]
        public static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int attrValue, int attrSize);
        public const int GWL_EXSTYLE = -20;
        public const int WS_EX_TOOLWINDOW = 0x00000080;
        public static readonly IntPtr HWND_TOPMOST = new IntPtr(-1);
        public static readonly IntPtr HWND_NOTOPMOST = new IntPtr(-2);
        public const uint SWP_NOMOVE = 0x0002;
        public const uint SWP_NOSIZE = 0x0001;
        public const uint SWP_NOACTIVATE = 0x0010;
        public const int DWMWA_USE_IMMERSIVE_DARK_MODE = 20;

        // ---- Global hotkeys (RegisterHotKey / UnregisterHotKey) -----------------
        [DllImport("user32.dll")]
        public static extern bool RegisterHotKey(IntPtr hWnd, int id, uint fsModifiers, uint vk);
        [DllImport("user32.dll")]
        public static extern bool UnregisterHotKey(IntPtr hWnd, int id);
        public const uint MOD_ALT = 0x1;
        public const uint MOD_CONTROL = 0x2;
        public const uint MOD_SHIFT = 0x4;
        public const uint MOD_WIN = 0x8;
        public const int WM_HOTKEY = 0x0312;
        public const int HOTKEY_ID_BASE = 0xB000;
        public const int HOTKEY_ID_TRANSLATE = 0xBFFF;

        // ---- Working-set trim ---------------------------------------------------
        [DllImport("kernel32.dll")]
        public static extern bool SetProcessWorkingSetSize(IntPtr hProcess, int min, int max);

        // ---- Console OEM code page ---------------------------------------------
        // powercfg writes its output using the system OEM code page (the active
        // console CP). On zh-CN Windows this is 936 (GBK); on other locales it
        // differs. Reading with the real OEM CP avoids mojibake / broken GUID
        // parsing on non-Chinese systems. Falls back to 936 if the call fails.
        [DllImport("kernel32.dll")]
        public static extern uint GetOEMCP();
        public static int GetOemCodePage()
        {
            try { int cp = (int)GetOEMCP(); return cp > 0 ? cp : 936; }
            catch { return 936; }
        }
    }

}
