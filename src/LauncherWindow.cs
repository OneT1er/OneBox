using System;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;

namespace PowerAudioManager
{
    /// <summary>
    /// 普通权限快捷启动窗口（--launcher &lt;parentHwnd&gt;）：SetParent 嵌入 admin 悬浮窗的容器 HWND。
    /// 拖放由本普通进程处理，绕过 admin UIPI 限制。
    /// </summary>
    public class LauncherWindow : Window
    {
        [DllImport("user32.dll")] static extern IntPtr SetParent(IntPtr child, IntPtr parent);
        [DllImport("user32.dll")] static extern int GetWindowLong(IntPtr h, int i);
        [DllImport("user32.dll")] static extern int SetWindowLong(IntPtr h, int i, int v);
        const int GWL_STYLE = -16;
        const int WS_CHILD = 0x40000000;
        const int WS_POPUP = unchecked((int)0x80000000);
        const int WS_CAPTION = 0x00C00000;
        const int WS_THICKFRAME = 0x00040000;

        StackPanel _panel;
        public LauncherWindow()
        {
            WindowStyle = WindowStyle.None;
            ResizeMode = ResizeMode.NoResize;
            ShowInTaskbar = false;
            ShowActivated = false;
            Background = new SolidColorBrush(Color.FromRgb(28, 26, 40));
            Width = 300; Height = 70;
            _panel = new StackPanel();
            Content = _panel;
            LauncherBar.Build(_panel, () => Rebuild());
        }

        void Rebuild() { _panel.Children.Clear(); LauncherBar.Build(_panel, () => Rebuild()); }

        public void EmbedTo(IntPtr parent)
        {
            var hwnd = new WindowInteropHelper(this).EnsureHandle();
            SetParent(hwnd, parent);
            int style = GetWindowLong(hwnd, GWL_STYLE);
            style &= ~(WS_POPUP | WS_CAPTION | WS_THICKFRAME);
            style |= WS_CHILD;
            SetWindowLong(hwnd, GWL_STYLE, style);
        }
    }
}
