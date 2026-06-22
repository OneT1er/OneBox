using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Microsoft.Win32;

namespace PowerAudioManager
{
    // Handles the floating window's resolution-aware scaling and work-area
    // clamping. Extracted from MainWindow so the window class doesn't carry
    // DPI / display-geometry plumbing.
    //
    // Scaling: linear map from the primary screen's physical width to a scale
    // factor (1.0 at 1920px, 1.5 at 3840px), applied to the window width and as
    // a LayoutTransform on the main border. Uses physical pixels
    // (Screen.Bounds), which update reliably across resolution switches
    // regardless of the process's DPI awareness.
    //
    // Clamping: keeps the window inside the current work area, snapping back to
    // the top-right corner if it ends up off-screen (e.g. monitor unplugged,
    // 4K -> 1080p switch).
    internal sealed class WindowScaling
    {
        const double BaseWindowWidth = 280.0;
        readonly Window _window;
        readonly Func<Border> _getMainBorder;
        double _currentScale = -1; // -1 forces first apply

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
                // scale = 1.0 at 1920px, 1.5 at 3840px (linear).
                double scale = 0.5 + (phys - 1920.0) * (0.5 / 1920.0);
                if (scale < 0.85) scale = 0.85;
                if (scale > 2.0) scale = 2.0;
                var mainBorder = _getMainBorder();
                if (scale == _currentScale && mainBorder != null && mainBorder.LayoutTransform != null) return;
                _currentScale = scale;
                _window.Width = BaseWindowWidth * scale;
                if (mainBorder != null)
                    mainBorder.LayoutTransform = new ScaleTransform(scale, scale);
            }
            catch (Exception ex) { AppLog.Log("ApplyScaling", ex); }
        }

        public void OnUserPreferenceChanged(object sender, UserPreferenceChangedEventArgs e)
        {
            // DPI / desktop changes can also move the work area on us.
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

        // Fixed position (unconditional): on a display-config change we do NOT
        // move the window — it stays exactly where the user left it, surviving
        // resolution / DPI switches. We only rescue it if it has become fully
        // off-screen (e.g. the monitor it lived on was unplugged), so the user
        // can never lose the window entirely. Partial overhang is left as-is.
        public void Reposition()
        {
            EnsureVisible();
        }

        // Nudge the window back on-screen only when it is completely outside the
        // current work area. Anything else (e.g. one edge poking off after a
        // resolution drop) is intentionally untouched to honour "固定位置".
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
                    // Completely lost — snap back to the top-right of the primary work area.
                    _window.Left = wa.Right - w - 20;
                    _window.Top  = wa.Top + 20;
                }
            }
            catch (Exception ex) { AppLog.Log("EnsureVisible", ex); }
        }

        // Work area in WPF DIPs (WinForms returns device pixels).
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

        // Kept for callers that still clamp on first layout / explicit re-clamp.
        // Behaviour matches EnsureVisible (rescue only when fully off-screen) so the
        // fixed-position guarantee holds on startup too.
        public void ClampToWorkArea()
        {
            EnsureVisible();
        }
    }
}
