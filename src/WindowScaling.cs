using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Microsoft.Win32;

namespace PowerAudioManager
{
    // 悬浮窗的缩放感知与工作区限制。从 MainWindow 抽取，避免窗口类承载 DPI/显示几何逻辑。
    //
    // 缩放：从主屏幕物理宽度到缩放因子的线性映射（1920px → 1.0, 3840px → 1.5），
    // 应用到窗口宽度和主 Border 的 LayoutTransform。使用物理像素（Screen.Bounds），
    // 分辨率切换时可靠更新，不受进程 DPI 感知模式影响。
    //
    // 限制：窗口保持在当前工作区内，若完全离开屏幕（如拔掉显示器、4K→1080p 切换）
    // 则弹回右上角。
    internal sealed class WindowScaling
    {
        const double BaseWindowWidth = 280.0;
        readonly Window _window;
        readonly Func<Border> _getMainBorder;
        double _currentScale = -1; // -1 强制首次应用
        double? _manualScale;

        public WindowScaling(Window window, Func<Border> getMainBorder)
        {
            _window = window;
            _getMainBorder = getMainBorder;
        }

        public void ApplyScaling()
        {
            try
            {
                double phys = 1920;
                try { phys = System.Windows.Forms.Screen.PrimaryScreen.Bounds.Width; }
                catch { }
                if (phys <= 0) phys = 1920;
                // scale = 1.0 at 1920px, 1.5 at 3840px（线性）—— 相对于 1080p 基准的总物理放大倍数。
                double scale = 0.5 + (phys - 1920.0) * (0.5 / 1920.0);
                if (scale < 0.85) scale = 0.85;
                if (scale > 2.0) scale = 2.0;

                if (_manualScale.HasValue)
                    scale = _manualScale.Value;
                else if (AppPrefs.GetDouble("WindowScale.Factor", out double saved) && saved >= 0.8 && saved <= 2.0)
                    scale = saved;

                // Per-Monitor V2 下 DIP→像素的 DPI 缩放已经放大了窗口（如 4K @150% 为 1.5x）。
                // 除以 DPI 缩放比，使 LayoutTransform 仅补充 DPI 未提供的放大 ——
                // 否则窗口会被双重缩放（过大）。
                double dpiScale = 1.0;
                try
                {
                    var src = PresentationSource.FromVisual(_window);
                    if (src != null && src.CompositionTarget != null)
                        dpiScale = src.CompositionTarget.TransformToDevice.M11;
                }
                catch { }
                if (dpiScale <= 0) dpiScale = 1.0;
                double layoutScale = scale / dpiScale;
                if (layoutScale < 0.85) layoutScale = 0.85;
                if (layoutScale > 2.0) layoutScale = 2.0;
                var mainBorder = _getMainBorder();
                if (layoutScale == _currentScale && mainBorder != null && mainBorder.LayoutTransform != null) return;
                _currentScale = layoutScale;
                _window.Width = BaseWindowWidth * layoutScale;
                if (mainBorder != null)
                    mainBorder.LayoutTransform = new ScaleTransform(layoutScale, layoutScale);
            }
            catch (Exception ex) { AppLog.Log("ApplyScaling", ex); }
        }

        public void ApplyManualScale(double scale)
        {
            if (scale < 0.8) scale = 0.8;
            if (scale > 2.0) scale = 2.0;
            _manualScale = scale;
            AppPrefs.SetDouble("WindowScale.Factor", scale);
            ApplyScaling();
        }

        public void ResetManualScale()
        {
            _manualScale = null;
            try { using (var k = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(@"Software\PowerAudioManager\App", true)) k?.DeleteValue("WindowScale.Factor", false); } catch { }
            _currentScale = -1; // 强制重新应用
            ApplyScaling();
        }

        public void OnUserPreferenceChanged(object sender, UserPreferenceChangedEventArgs e)
        {
            // DPI/桌面变更可能移动工作区。
            if (e.Category == UserPreferenceCategory.Desktop ||
                e.Category == UserPreferenceCategory.General)
            {
                try { _window.Dispatcher.BeginInvoke(new Action(() => { ApplyScaling(); Reposition(); })); } catch { }
            }
        }

        public void OnDisplaySettingsChanged(object sender, EventArgs e)
        {
            try { _window.Dispatcher.BeginInvoke(new Action(() => { ApplyScaling(); Reposition(); })); } catch { }
        }

        // 固定位置（无条件）：显示配置变更时窗口不移动，保持在用户放置的原位，
        // 分辨率/DPI 切换后位置不变。仅在窗口完全离开屏幕（如所在显示器被拔掉）
        // 时救回可见区域，防止用户彻底丢失窗口。部分超出屏幕则不做处理。
        public void Reposition()
        {
            EnsureVisible();
        }

        // 仅在窗口完全超出当前工作区时将其推回屏幕内。部分超出（如分辨率降低后
        // 一条边露出屏幕）刻意不处理，以遵循"固定位置"策略。
        public void EnsureVisible()
        {
            try
            {
                var wa = GetWorkAreaDip();
                double w = _window.ActualWidth > 0 ? _window.ActualWidth : _window.Width;
                double h = _window.ActualHeight > 0 ? _window.ActualHeight : _window.Height;
                if (double.IsNaN(w) || w <= 0) w = 280;
                if (double.IsNaN(h) || h <= 0) h = 36;
                double left = _window.Left;
                double top = _window.Top;
                bool offscreen = double.IsNaN(left) || double.IsNaN(top)
                    || left + w <= wa.Left + 8 || left >= wa.Right - 8
                    || top + h <= wa.Top + 8  || top >= wa.Bottom - 8;
                if (offscreen)
                {
                    _window.Left = wa.Right - w - 20;
                    _window.Top  = wa.Top + 20;
                }
            }
            catch (Exception ex) { AppLog.Log("EnsureVisible", ex); }
        }

        // 工作区转 WPF DIP（WinForms 返回设备像素）。
        struct Rect { public double Left, Top, Right, Bottom; public double Width { get { return Right - Left; } } public double Height { get { return Bottom - Top; } } }
        Rect GetWorkAreaDip()
        {
            var screen = System.Windows.Forms.Screen.PrimaryScreen;
            double waLeft = screen.WorkingArea.Left;
            double waTop = screen.WorkingArea.Top;
            double waRight = screen.WorkingArea.Right;
            double waBottom = screen.WorkingArea.Bottom;
            double dpi = 96.0;
            try
            {
                var src = PresentationSource.FromVisual(_window);
                if (src != null && src.CompositionTarget != null)
                    dpi = 96.0 * src.CompositionTarget.TransformToDevice.M11;
            }
            catch { }
            double scale = 96.0 / dpi;
            return new Rect { Left = waLeft * scale, Top = waTop * scale, Right = waRight * scale, Bottom = waBottom * scale };
        }

        // 保留给仍需首次布局/显式重新限制的调用方。行为与 EnsureVisible 一致（仅完全离开屏幕时救回），
        // 确保启动时也遵循固定位置策略。
        public void ClampToWorkArea()
        {
            EnsureVisible();
        }
    }
}
