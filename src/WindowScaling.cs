using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Microsoft.Win32;

namespace PowerAudioManager
{
    // 悬浮窗的缩放感知与工作区限制。从 MainWindow 抽取，避免窗口类承载 DPI/显示几何逻辑。
    //
    // 缩放策略：
    //   - 以 1920×1080 (1080p) 为基准 (auto scale = 1.0)，按屏幕对角线像素数用幂曲线平滑缩放。
    //     1080p=1.00  2K=1.19  4K=1.52  8K=2.30(钳到 1.6)  1366×768=0.81  800×600=0.74。
    //   - WPF PerMonitorV2 已按 DPI 自动缩放内容，LayoutTransform 不再除以 DPI 缩放比
    //     （旧的除法在 1080p+150% DPI、4K+200% DPI 等常见组合下被钳到下限 0.85，对绝大多数用户失效）。
    //   - 通过 LayoutTransform 整体放大 Border 内子元素（文字、图标、Padding），同时显式缩放
    //     Border 自身的 CornerRadius/BorderThickness/Effect，保证 1080p↔4K 之间观感一致。
    //   - 使用窗口当前所在屏幕（多显示器按覆盖面积匹配），非主屏。
    //
    // 限制：窗口保持在当前工作区内，若完全离开屏幕则弹回右上角（"固定位置"策略）。
    internal sealed class WindowScaling
    {
        // 基础设计尺寸：以 1080p 100% DPI 为 1.0 scale 时的参考值。
        const double BaseWindowWidth = 280.0;
        const double BaseCornerRadius = 10.0;
        const double BaseBorderThickness = 1.0;
        const double BaseShadowBlur = 36.0;
        const double BaseShadowDepth = 2.0;
        // 1080p 对角线像素数 = sqrt(1920² + 1080²) ≈ 2202.9
        const double ReferenceDiagonal = 2202.907;
        // 缩放幂指数（<1 让分辨率差异比线性更柔和）
        const double ScaleExponent = 0.6;
        // 自动缩放范围；手动滑块上限放宽到 2.0
        const double AutoMin = 0.7;
        const double AutoMax = 1.6;
        const double FinalMin = 0.7;
        const double FinalMax = 2.0;

        readonly Window _window;
        readonly Func<Border> _getMainBorder;
        double _currentScale = -1; // -1 强制首次应用
        double? _manualScale;
        double _lastAutoScale = 1.0;

        // 暴露给设置面板：当前实际应用的缩放、当前自动计算值、所在屏幕描述、是否自动模式。
        public double CurrentScale => _currentScale;
        public double AutoScale => _lastAutoScale;
        public string CurrentScreenDescription { get; private set; } = "";
        public bool IsAuto
        {
            get
            {
                if (_manualScale.HasValue) return false;
                if (AppPrefs.GetDouble("WindowScale.Factor", out double v) && v >= 0.8 && v <= 2.0) return false;
                return true;
            }
        }

        public WindowScaling(Window window, Func<Border> getMainBorder)
        {
            _window = window;
            _getMainBorder = getMainBorder;
        }

        public void ApplyScaling()
        {
            try
            {
                // 1) 取窗口所在屏幕（多显示器按覆盖面积最大匹配），非主屏。
                var screen = GetCurrentScreen();
                double physW = 1920, physH = 1080;
                if (screen != null)
                {
                    physW = screen.Bounds.Width;
                    physH = screen.Bounds.Height;
                }
                if (physW <= 0) physW = 1920;
                if (physH <= 0) physH = 1080;
                CurrentScreenDescription = $"{(int)physW}×{(int)physH}";

                // 2) 按对角线像素的幂曲线计算 auto scale，1080p = 1.0。
                double diagonal = Math.Sqrt(physW * physW + physH * physH);
                double autoScale = Math.Pow(diagonal / ReferenceDiagonal, ScaleExponent);
                if (autoScale < AutoMin) autoScale = AutoMin;
                if (autoScale > AutoMax) autoScale = AutoMax;
                _lastAutoScale = autoScale;

                // 3) 应用手动覆盖（运行时 > 注册表 > 自动）
                double scale = autoScale;
                if (_manualScale.HasValue)
                    scale = _manualScale.Value;
                else if (AppPrefs.GetDouble("WindowScale.Factor", out double saved) && saved >= 0.8 && saved <= 2.0)
                    scale = saved;

                if (scale < FinalMin) scale = FinalMin;
                if (scale > FinalMax) scale = FinalMax;

                // 4) 应用到窗口和 Border。
                // WPF PerMonitorV2 已按 DPI 自动缩放内容，LayoutTransform 不再除以 DPI 缩放比——
                // 旧的除法在 1080p+150% DPI、4K+200% DPI 等常见组合下会被钳到下限，缩放功能对绝大多数用户失效。
                // 视觉效果：4K@200% DPI 用户得到 1.52x 额外缩放 + 2.0x DPI 缩放 = 较大的悬浮窗，符合高分辨率预期。
                var mainBorder = _getMainBorder();
                if (mainBorder != null && Math.Abs(scale - _currentScale) < 0.005 && mainBorder.LayoutTransform != null)
                {
                    // 缩放未变但 Border 可能被重建（RebuildUI），仍要同步视觉属性。
                    ApplyBorderVisuals(mainBorder, scale);
                    return;
                }
                _currentScale = scale;
                _window.Width = BaseWindowWidth * scale;
                if (mainBorder != null)
                {
                    mainBorder.LayoutTransform = new ScaleTransform(scale, scale);
                    ApplyBorderVisuals(mainBorder, scale);
                }
            }
            catch (Exception ex) { AppLog.Log("ApplyScaling", ex); }
        }

        // 同步 Border 自身的视觉属性（圆角、边线、阴影），与 LayoutTransform 配合保证观感一致。
        // LayoutTransform 会缩放子元素渲染，但 Border 自身的 CornerRadius/BorderThickness/Effect 需显式同步——
        // 否则缩放变化时这些属性保持原值，与已缩放的内容比例失调。
        void ApplyBorderVisuals(Border border, double scale)
        {
            border.CornerRadius = new CornerRadius(Math.Max(2.0, BaseCornerRadius * scale));
            border.BorderThickness = new Thickness(Math.Max(1.0, BaseBorderThickness * scale));
            if (border.Effect is System.Windows.Media.Effects.DropShadowEffect eff)
            {
                eff.BlurRadius = BaseShadowBlur * scale;
                eff.ShadowDepth = BaseShadowDepth * scale;
                // Opacity 与尺寸无关，保持原值
            }
        }

        // 找窗口当前所在的屏幕（按覆盖面积最大匹配），找不到时回退 PrimaryScreen。
        // 用于缩放公式和工作区限制。
        System.Windows.Forms.Screen GetCurrentScreen()
        {
            try
            {
                double left = _window.Left;
                double top = _window.Top;
                double w = _window.ActualWidth > 0 ? _window.ActualWidth : _window.Width;
                double h = _window.ActualHeight > 0 ? _window.ActualHeight : _window.Height;
                if (double.IsNaN(w) || w <= 0) w = BaseWindowWidth;
                if (double.IsNaN(h) || h <= 0) h = 36;
                if (double.IsNaN(left)) left = 0;
                if (double.IsNaN(top)) top = 0;
                // DIP→物理像素：用窗口自己的 DPI 缩放比（多显示器各屏 DPI 独立）
                double dpiScale = GetDpiScale();
                var rect = new System.Drawing.Rectangle(
                    (int)(left * dpiScale), (int)(top * dpiScale),
                    (int)(Math.Max(1, w) * dpiScale), (int)(Math.Max(1, h) * dpiScale));
                System.Windows.Forms.Screen best = null;
                int bestArea = 0;
                foreach (var s in System.Windows.Forms.Screen.AllScreens)
                {
                    var inter = System.Drawing.Rectangle.Intersect(rect, s.Bounds);
                    int area = inter.Width * inter.Height;
                    if (area > bestArea) { bestArea = area; best = s; }
                }
                if (best != null) return best;
            }
            catch { }
            return System.Windows.Forms.Screen.PrimaryScreen;
        }

        double GetDpiScale()
        {
            try
            {
                var src = PresentationSource.FromVisual(_window);
                if (src != null && src.CompositionTarget != null)
                    return src.CompositionTarget.TransformToDevice.M11;
            }
            catch { }
            return 1.0;
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
                if (double.IsNaN(w) || w <= 0) w = BaseWindowWidth;
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
            // 工作区跟随窗口所在屏幕（多显示器时），与缩放公式保持一致
            var screen = GetCurrentScreen() ?? System.Windows.Forms.Screen.PrimaryScreen;
            double waLeft = screen.WorkingArea.Left;
            double waTop = screen.WorkingArea.Top;
            double waRight = screen.WorkingArea.Right;
            double waBottom = screen.WorkingArea.Bottom;
            double dpiScale = GetDpiScale();
            if (dpiScale <= 0) dpiScale = 1.0;
            double scale = 1.0 / dpiScale;
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
