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
    // 折叠/自动折叠：手动与自动折叠状态机、拖入自动展开、UIPI 拖放消息过滤。
    public partial class MainWindow : Window
    {
        void ToggleCollapse(object sender, RoutedEventArgs e)
        {
            SetExpanded(!_isExpanded, true);
        }

        // admin 运行时 UIPI 默认阻止普通进程（资源管理器/浏览器）拖放进来，放宽消息过滤
        [System.Runtime.InteropServices.DllImport("user32.dll")] static extern bool ChangeWindowMessageFilterEx(IntPtr hwnd, uint msg, uint flag, IntPtr pChangeFilterStruct);
        const uint WM_DROPFILES = 0x0233;
        const uint WM_COPYGLOBALDATA = 0x0049;
        const uint MSGFLT_ALLOW = 1;

        void OnWindowDragEnter(DragEventArgs e)
        {
            bool ok = LauncherBar.HasDropData(e.Data);
            if (ok && !_isExpanded) SetExpanded(true, false);
            e.Effects = ok ? DragDropEffects.Copy : DragDropEffects.None;
            e.Handled = true;
        }

        void SetExpanded(bool expanded) { SetExpanded(expanded, false); }

        void SetExpanded(bool expanded, bool manual)
        {
            _isExpanded = expanded;
            if (manual && !expanded) _collapsedManually = true;
            if (expanded) _collapsedManually = false;
            // 固定底边使窗口向上折叠
            double bottom = Top + ActualHeight;
            if (_isExpanded)
            {
                if (_contentPanel != null) _contentPanel.Visibility = Visibility.Visible;
                // 展开时标题栏下角保持直角与内容区衔接。
                if (_titleBarBorder != null) _titleBarBorder.CornerRadius = new CornerRadius(10, 10, 0, 0);
                if (_collapsedTempLabel != null) _collapsedTempLabel.Visibility = Visibility.Collapsed;
                SizeToContent = SizeToContent.Height;
            }
            else
            {
                if (_contentPanel != null) _contentPanel.Visibility = Visibility.Collapsed;
                // 折叠时仅标题栏可见，下角也圆角匹配外层卡片，避免方角超出圆角弧边。
                if (_titleBarBorder != null) _titleBarBorder.CornerRadius = new CornerRadius(10);
                if (_collapsedTempLabel != null && ModuleVisible("Temp"))
                    _collapsedTempLabel.Visibility = Visibility.Visible;
                SizeToContent = SizeToContent.Height;
                MinHeight = 36;
            }
            // 重锚定：保持底边固定，等 LayoutUpdated 后 ActualHeight 正确再调整。
            EventHandler reanchor = null;
            reanchor = (xs, xe) =>
            {
                LayoutUpdated -= reanchor;
                double newTop = bottom - ActualHeight;
                var wa = SystemParameters.WorkArea;
                if (newTop < wa.Top) newTop = wa.Top;
                if (newTop + ActualHeight > wa.Bottom) newTop = wa.Bottom - ActualHeight;
                Top = newTop;
            };
            LayoutUpdated += reanchor;
            // 手动展开时取消待执行的自动折叠。
            if (expanded && _autoCollapseTimer != null) _autoCollapseTimer.Stop();
        }

        void StartAutoCollapse()
        {
            if (!AppPrefs.GetBool("AutoCollapse", true)) return;
            _autoCollapseTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(Math.Max(0, AppPrefs.GetInt("AutoCollapseDelay", 8))) };
            _autoCollapseTimer.Tick += (s, e) => { _autoCollapseTimer.Stop(); SetExpanded(false, false); };
            MouseEnter += (s, e) =>
            {
                if (_autoCollapseTimer != null) _autoCollapseTimer.Stop();
                if (!_isExpanded)
                {
                    // 手动折叠后悬停不展开，除非开启"手动折叠后也自动展开"。
                    if (_collapsedManually && !AppPrefs.GetBool("AutoExpandAfterManual", false)) return;
                    SetExpanded(true);
                }
            };
            MouseLeave += (s, e) =>
            {
                // 手动折叠的窗口不再自动折叠（已折叠且用户希望保持折叠）。
                if (_collapsedManually) return;
                if (_autoCollapseTimer != null && AppPrefs.GetBool("AutoCollapse", true))
                {
                    _autoCollapseTimer.Interval = TimeSpan.FromSeconds(Math.Max(0, AppPrefs.GetInt("AutoCollapseDelay", 8)));
                    _autoCollapseTimer.Start();
                }
            };
        }

        internal void RefreshAutoCollapse()
        {
            if (_autoCollapseTimer != null) _autoCollapseTimer.Stop();
            if (!AppPrefs.GetBool("AutoCollapse", true)) return;
            if (_autoCollapseTimer == null) { StartAutoCollapse(); return; }
            _autoCollapseTimer.Interval = TimeSpan.FromSeconds(Math.Max(0, AppPrefs.GetInt("AutoCollapseDelay", 8)));
        }
    }
}

