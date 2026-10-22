using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;

namespace PowerAudioManager
{
    internal enum ButtonVariant { Flat, Primary, Secondary, Danger }

    // Shared building blocks for the direction-A ("紫影精修") UI.  Every window
    // composes its surfaces from these helpers so spacing, radii and colors
    // stay consistent without a XAML resource dictionary.
    internal static class UiKit
    {
        // 共享调色板（别名保持旧调用点可用）。
        internal static readonly Color AccentColor = ThemeTokens.Accent;
        internal static readonly Color BgColor = ThemeTokens.Background;
        internal static readonly Color CardColor = ThemeTokens.Card;
        internal static readonly Color RaisedColor = ThemeTokens.Raised;
        internal static readonly Color TextPrimary = ThemeTokens.PrimaryText;
        internal static readonly Color TextSecondary = ThemeTokens.SecondaryText;
        internal static readonly Color TextTertiary = ThemeTokens.TertiaryText;
        internal static readonly Color TextDisabled = ThemeTokens.DisabledText;
        internal static readonly Color ActiveBg = ThemeTokens.Active;
        internal static readonly Color BorderColor = ThemeTokens.Border;
        internal static readonly Color BorderStrongColor = ThemeTokens.BorderStrong;
        internal static readonly Color WarnColor = ThemeTokens.Warn;
        internal static readonly Color WarnSurface = ThemeTokens.WarnSurface;
        internal static readonly Color DangerColor = ThemeTokens.Danger;
        internal static readonly Color DangerSurface = ThemeTokens.DangerSurface;
        internal static readonly Color SuccessColor = ThemeTokens.Success;
        internal static readonly Color SuccessSurface = ThemeTokens.SuccessSurface;

        internal static readonly FontFamily MonoFont = new FontFamily("Cascadia Mono, Consolas");

        // 按颜色缓存冻结画笔：温度行每秒刷新频繁，复用冻结 SolidColorBrush 避免每秒 new 一批 Freezable。
        static readonly Dictionary<Color, SolidColorBrush> _frozenBrushes = new Dictionary<Color, SolidColorBrush>();
        internal static SolidColorBrush FrozenBrush(Color c)
        {
            if (!_frozenBrushes.TryGetValue(c, out var b)) { b = new SolidColorBrush(c); b.Freeze(); _frozenBrushes[c] = b; }
            return b;
        }

        // ---- surfaces -------------------------------------------------------

        // Grouped card: the main container for a section (radius 10, Card fill).
        internal static Border Card(UIElement child, Thickness? padding = null, double radius = 10)
        {
            return new Border
            {
                Background = FrozenBrush(CardColor),
                CornerRadius = new CornerRadius(radius),
                Padding = padding ?? new Thickness(10),
                Child = child
            };
        }

        // Hairline between rows inside a card (inset 12px).
        internal static Border MakeRowDivider()
        {
            return new Border { Height = 1, Background = FrozenBrush(RaisedColor), Margin = new Thickness(12, 0, 12, 0) };
        }

        // Legacy divider kept for call sites that separate free-standing blocks.
        internal static Border MakeDivider()
        {
            return new Border
            {
                Height = 1,
                Background = FrozenBrush(BorderColor),
                Margin = new Thickness(2, 8, 2, 8)
            };
        }

        // Card header: accent icon + bold title + optional trailing element.
        internal static DockPanel SectionHeader(IconKey icon, string title, UIElement trailing = null)
        {
            var dock = new DockPanel { LastChildFill = true, Background = Brushes.Transparent };
            var glyph = IconCatalog.CreateElement(icon, 14, FrozenBrush(AccentColor));
            glyph.Margin = new Thickness(0, 0, 6, 0);
            glyph.VerticalAlignment = VerticalAlignment.Center;
            DockPanel.SetDock(glyph, Dock.Left);
            dock.Children.Add(glyph);
            if (trailing != null)
            {
                DockPanel.SetDock(trailing, Dock.Right);
                dock.Children.Add(trailing);
            }
            dock.Children.Add(Text(title, 12, TextPrimary, FontWeights.SemiBold));
            return dock;
        }

        // Small grey caption above a settings group.
        internal static TextBlock GroupCaption(string text)
        {
            var t = Text(text, 11, TextTertiary, FontWeights.SemiBold);
            t.Margin = new Thickness(4, 0, 0, 6);
            return t;
        }

        internal static TextBlock Text(string text, double size = 12, Color? color = null, FontWeight? weight = null)
        {
            return new TextBlock
            {
                Text = text,
                FontSize = size,
                Foreground = FrozenBrush(color ?? TextSecondary),
                FontWeight = weight ?? FontWeights.Normal,
                VerticalAlignment = VerticalAlignment.Center,
                TextTrimming = TextTrimming.CharacterEllipsis
            };
        }

        // Settings group = caption + card holding rows separated by hairlines.
        internal static StackPanel SettingsGroup(string caption, params UIElement[] rows)
        {
            var outer = new StackPanel { Margin = new Thickness(0, 0, 0, 14) };
            if (!string.IsNullOrEmpty(caption)) outer.Children.Add(GroupCaption(caption));
            var inner = new StackPanel();
            for (int i = 0; i < rows.Length; i++)
            {
                if (rows[i] == null) continue;
                if (inner.Children.Count > 0) inner.Children.Add(MakeRowDivider());
                inner.Children.Add(rows[i]);
            }
            outer.Children.Add(Card(inner, new Thickness(0)));
            return outer;
        }

        // A 44px settings row: label (+ optional description) on the left,
        // control on the right.
        internal static DockPanel SettingsRow(string label, UIElement control, string description = null)
        {
            var row = new DockPanel { LastChildFill = true, MinHeight = description == null ? 44 : 52, Margin = new Thickness(12, 0, 12, 0) };
            if (control != null)
            {
                if (control is FrameworkElement fe) { fe.VerticalAlignment = VerticalAlignment.Center; fe.Margin = new Thickness(12, 0, 0, 0); }
                DockPanel.SetDock(control, Dock.Right);
                row.Children.Add(control);
            }
            var texts = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
            var title = Text(label, 12, TextPrimary);
            title.TextWrapping = TextWrapping.Wrap;
            title.TextTrimming = TextTrimming.None;
            texts.Children.Add(title);
            if (!string.IsNullOrEmpty(description))
            {
                var d = Text(description, 11, TextSecondary);
                d.TextWrapping = TextWrapping.Wrap;
                d.TextTrimming = TextTrimming.None;
                d.Margin = new Thickness(0, 2, 0, 0);
                texts.Children.Add(d);
            }
            row.Children.Add(texts);
            return row;
        }

        // ---- controls -------------------------------------------------------

        internal static void ApplyButtonVariant(Button btn, ButtonVariant variant)
        {
            string key = variant switch
            {
                ButtonVariant.Primary => ThemeTokens.PrimaryButtonKey,
                ButtonVariant.Secondary => ThemeTokens.SecondaryButtonKey,
                ButtonVariant.Danger => ThemeTokens.DangerButtonKey,
                _ => ThemeTokens.FlatButtonKey
            };
            if (Application.Current?.TryFindResource(key) is Style style) btn.Style = style;
        }

        internal static Button MakeButton(string text, ButtonVariant variant = ButtonVariant.Secondary, RoutedEventHandler onClick = null)
        {
            var btn = new Button { Content = text, Height = 32 };
            ApplyButtonVariant(btn, variant);
            if (onClick != null) btn.Click += onClick;
            return btn;
        }

        // Apply the shared local button style.  A local style keeps every window
        // consistent even when the optional visual package is absent.
        internal static void ApplyFlatStyle(Button btn)
        {
            var style = Application.Current?.TryFindResource(ThemeTokens.FlatButtonKey) as Style;
            if (style != null) btn.Style = style;
            else
            {
                btn.Background = Brushes.Transparent;
                btn.BorderBrush = Brushes.Transparent;
            }
        }

        // Compact icon button (title bar, mute, launcher).  Keep the hit target
        // at least 28px while the vector itself remains on the 16px grid.
        internal static void ApplyIconButtonStyle(Button btn)
        {
            ApplyFlatStyle(btn);
            btn.MinWidth = 28;
            btn.MinHeight = 28;
            btn.Padding = new Thickness(0);
            btn.BorderBrush = Brushes.Transparent;
            btn.HorizontalContentAlignment = HorizontalAlignment.Center;
            btn.VerticalContentAlignment = VerticalAlignment.Center;
        }

        internal static Button MakeIconButton(IconKey icon, string tooltip, double size = 32, double glyph = 16, Color? color = null)
        {
            var btn = new Button
            {
                Width = size, Height = size,
                Foreground = FrozenBrush(color ?? TextSecondary),
                Content = IconCatalog.CreateElement(icon, glyph, FrozenBrush(color ?? TextSecondary)),
                ToolTip = tooltip
            };
            ApplyIconButtonStyle(btn);
            AutomationProperties.SetName(btn, tooltip);
            return btn;
        }

        // CheckBox rendered as a switch (label left, switch right).
        internal static CheckBox MakeSwitch(string label, bool isOn, Action<bool> changed = null)
        {
            var cb = new CheckBox { Content = label, IsChecked = isOn };
            if (Application.Current?.TryFindResource(ThemeTokens.SwitchKey) is Style style) cb.Style = style;
            if (label == null) AutomationProperties.SetName(cb, "开关");
            if (changed != null)
            {
                cb.Checked += (_, _) => changed(true);
                cb.Unchecked += (_, _) => changed(false);
            }
            return cb;
        }

        // Segmented control: equal-width segments inside a dark pill.
        internal static Border MakeSegmented(IReadOnlyList<string> labels, int selected, Action<int> picked, string accessibleName = null)
        {
            var grid = new UniformGrid { Rows = 1, Columns = labels.Count };
            string group = "seg" + Guid.NewGuid().ToString("N");
            for (int i = 0; i < labels.Count; i++)
            {
                int index = i;
                var rb = new RadioButton { Content = labels[i], GroupName = group, IsChecked = i == selected, Margin = new Thickness(1, 0, 1, 0) };
                if (Application.Current?.TryFindResource(ThemeTokens.SegmentKey) is Style style) rb.Style = style;
                rb.Checked += (_, _) => picked?.Invoke(index);
                grid.Children.Add(rb);
            }
            var host = new Border
            {
                Background = FrozenBrush(BgColor),
                CornerRadius = new CornerRadius(8),
                Padding = new Thickness(2),
                Child = grid
            };
            if (accessibleName != null) AutomationProperties.SetName(host, accessibleName);
            return host;
        }

        // "磁贴" slider (28px block, fill = value, accent edge).
        internal static Slider MakeTileSlider(double min, double max, double value, double height = 28)
        {
            var slider = new Slider { Minimum = min, Maximum = max, Value = value, Height = height, VerticalAlignment = VerticalAlignment.Center };
            if (Application.Current?.TryFindResource(ThemeTokens.TileSliderKey) is Style style) slider.Style = style;
            return slider;
        }

        // Monospace key-cap chip used for hotkeys.
        internal static Border KeyChip(string text)
        {
            return new Border
            {
                Background = FrozenBrush(RaisedColor),
                CornerRadius = new CornerRadius(6),
                Padding = new Thickness(8, 3, 8, 3),
                VerticalAlignment = VerticalAlignment.Center,
                Child = new TextBlock { Text = text, FontFamily = MonoFont, FontSize = 11, Foreground = FrozenBrush(TextPrimary) }
            };
        }

        // Small rounded tag, e.g. 「可能卡顿」.
        internal static Border Tag(string text, Color fg, Color bg)
        {
            return new Border
            {
                Background = FrozenBrush(bg),
                CornerRadius = new CornerRadius(4),
                Padding = new Thickness(6, 1, 6, 1),
                Margin = new Thickness(6, 0, 0, 0),
                VerticalAlignment = VerticalAlignment.Center,
                Child = new TextBlock { Text = text, FontSize = 10, Foreground = FrozenBrush(fg) }
            };
        }

        // Dark single-line input matching the design (30px, radius 6).
        internal static TextBox MakeTextBox(string text = "", double width = double.NaN)
        {
            var tb = new TextBox
            {
                Text = text ?? "",
                Width = width,
                MinHeight = 30,
                Padding = new Thickness(8, 5, 8, 5),
                Background = FrozenBrush(BgColor),
                Foreground = FrozenBrush(TextPrimary),
                CaretBrush = FrozenBrush(TextPrimary),
                BorderBrush = FrozenBrush(BorderStrongColor),
                BorderThickness = new Thickness(1),
                FontSize = 12,
                VerticalContentAlignment = VerticalAlignment.Center
            };
            tb.Template = RoundedTextBoxTemplate();
            return tb;
        }

        static ControlTemplate _roundedTextBox;
        static ControlTemplate RoundedTextBoxTemplate()
        {
            if (_roundedTextBox != null) return _roundedTextBox;
            var template = new ControlTemplate(typeof(TextBox));
            var border = new FrameworkElementFactory(typeof(Border));
            border.Name = "Chrome";
            border.SetValue(Border.CornerRadiusProperty, new CornerRadius(6));
            border.SetValue(Border.BackgroundProperty, new TemplateBindingExtension(Control.BackgroundProperty));
            border.SetValue(Border.BorderBrushProperty, new TemplateBindingExtension(Control.BorderBrushProperty));
            border.SetValue(Border.BorderThicknessProperty, new TemplateBindingExtension(Control.BorderThicknessProperty));
            var host = new FrameworkElementFactory(typeof(ScrollViewer));
            host.Name = "PART_ContentHost";
            host.SetValue(FrameworkElement.MarginProperty, new TemplateBindingExtension(Control.PaddingProperty));
            host.SetValue(Control.VerticalContentAlignmentProperty, new TemplateBindingExtension(Control.VerticalContentAlignmentProperty));
            border.AppendChild(host);
            template.VisualTree = border;
            var focus = new Trigger { Property = UIElement.IsKeyboardFocusWithinProperty, Value = true };
            focus.Setters.Add(new Setter(Border.BorderBrushProperty, FrozenBrush(AccentColor), "Chrome"));
            template.Triggers.Add(focus);
            template.Seal();
            _roundedTextBox = template;
            return template;
        }

        // −/+ number stepper (e.g. 「1.5 秒」).
        internal static Border MakeStepper(Func<string> display, Action decrement, Action increment)
        {
            var label = Text(display(), 12, TextPrimary);
            label.Width = 52;
            label.TextAlignment = TextAlignment.Center;
            Button Step(string glyph, string name, Action act)
            {
                var b = new Button { Content = glyph, Width = 28, Height = 28, FontSize = 14, ToolTip = name };
                ApplyIconButtonStyle(b);
                AutomationProperties.SetName(b, name);
                b.Click += (_, _) => { act(); label.Text = display(); };
                return b;
            }
            var row = new StackPanel { Orientation = Orientation.Horizontal };
            row.Children.Add(Step("−", "减少", decrement));
            row.Children.Add(label);
            row.Children.Add(Step("+", "增加", increment));
            return new Border
            {
                Background = FrozenBrush(BgColor),
                BorderBrush = FrozenBrush(BorderStrongColor),
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(6),
                Child = row
            };
        }

        // Status dot (drawn Ellipse) + text, e.g. a colored dot followed by 「OneBoxSvc 已接管特权清理」.
        internal static StackPanel StatusLine(string text, Color dot)
        {
            var sp = new StackPanel { Orientation = Orientation.Horizontal };
            sp.Children.Add(new System.Windows.Shapes.Ellipse { Width = 6, Height = 6, Fill = FrozenBrush(dot), Margin = new Thickness(0, 0, 6, 0), VerticalAlignment = VerticalAlignment.Center });
            sp.Children.Add(Text(text, 11, TextSecondary));
            return sp;
        }

        // One-off stroke glyph from 24x24 path data, for symbols the shared
        // IconCatalog does not carry (search, copy, pin, play...).  The
        // catalog's key set is pinned by tests and the asset manifest, so
        // ad-hoc glyphs live here instead.  Geometries are parsed once.
        static readonly Dictionary<string, Geometry> _glyphCache = new Dictionary<string, Geometry>();
        internal static System.Windows.Shapes.Path Glyph(string pathData, double size, Brush stroke, double thickness = 2, Brush fill = null)
        {
            if (!_glyphCache.TryGetValue(pathData, out var geometry))
            {
                geometry = Geometry.Parse(pathData);
                geometry.Freeze();
                _glyphCache[pathData] = geometry;
            }
            return new System.Windows.Shapes.Path
            {
                Data = geometry,
                Width = size, Height = size,
                Stretch = Stretch.Uniform,
                Stroke = stroke,
                Fill = fill ?? Brushes.Transparent,
                StrokeThickness = thickness * size / 24.0,
                StrokeStartLineCap = PenLineCap.Round,
                StrokeEndLineCap = PenLineCap.Round,
                StrokeLineJoin = PenLineJoin.Round,
                SnapsToDevicePixels = true
            };
        }

        // Common ad-hoc glyph path data (24x24 grid).
        internal static class Glyphs
        {
            internal const string Search = "M11,4 A7,7 0 1 1 10.99,4 Z M20,20 L16.5,16.5";
            internal const string Copy = "M9,9 L21,9 L21,21 L9,21 Z M5,15 L3,15 L3,3 L15,3 L15,5";
            internal const string Pin = "M9,3 L15,3 L14,9 L18,13 L6,13 L10,9 Z M12,13 L12,21";
            internal const string Check = "M5,12 L10,17 L19,7";
            internal const string Play = "M7,4 L19,12 L7,20 Z";
            internal const string Swap = "M7,7 L20,7 L16,3 M17,17 L4,17 L8,21";
            internal const string Minus = "M5,12 L19,12";
            internal const string Plus = "M12,5 L12,19 M5,12 L19,12";
            internal const string More = "M5,12 L5.01,12 M12,12 L12.01,12 M19,12 L19.01,12";
            internal const string Eye = "M2,12 C5,6 19,6 22,12 C19,18 5,18 2,12 Z M12,9 A3,3 0 1 1 11.99,9 Z";
            internal const string Refresh = "M21,12 A9,9 0 1 1 18,5.3 L21,8 M21,3 L21,8 L16,8";
            internal const string Help = "M12,3 A9,9 0 1 1 11.99,3 Z M9.5,9 A2.5,2.5 0 1 1 13,11.3 C12.4,11.6 12,12.2 12,13 M12,17 L12.01,17";
            internal const string Window = "M3,4 L21,4 L21,20 L3,20 Z M3,9 L21,9";
            internal const string Exit = "M12,2 L12,12 M18.4,6.6 A9,9 0 1 1 5.6,6.6";
            internal const string Grip = "M9,5 L9.01,5 M15,5 L15.01,5 M9,12 L9.01,12 M15,12 L15.01,12 M9,19 L9.01,19 M15,19 L15.01,19";
            internal const string Thermometer = "M14,14.8 L14,4 A2,2 0 0 0 10,4 L10,14.8 A4,4 0 1 0 14,14.8 Z";
            internal const string Headphones = "M3,18 L3,12 A9,9 0 0 1 21,12 L21,18 M21,19 A2,2 0 0 1 19,21 L18,21 L18,15 L21,15 Z M3,19 A2,2 0 0 0 5,21 L6,21 L6,15 L3,15 Z";
        }

        // All app icons come from the compiled vector catalog.
        internal static FrameworkElement PinIcon(bool locked, Brush brush = null)
            => IconCatalog.CreateElement(locked ? IconKey.Lock : IconKey.Unlock, 16, brush);

        internal static FrameworkElement MuteIcon(bool muted, Brush brush = null)
            => IconCatalog.CreateElement(muted ? IconKey.Mute : IconKey.Audio, 16, brush);

        // 彩色矢量图标（设置对话框与悬浮窗指标行复用）
        internal static FrameworkElement MetricIcon(string iconKey, Color c, double size = 14)
        {
            IconKey key = iconKey switch
            {
                "cpu" => IconKey.Cpu, "gpu" => IconKey.Gpu, "hot" => IconKey.Hot,
                "vram" => IconKey.Vram, "dram" => IconKey.Dram, "disk" => IconKey.Disk,
                "fan" => IconKey.Fan, "ctrl" => IconKey.Control, "mb" => IconKey.Motherboard,
                _ => IconKey.DefaultMetric
            };
            var view = IconCatalog.CreateElement(key, size, FrozenBrush(c));
            view.VerticalAlignment = VerticalAlignment.Center;
            view.Margin = new Thickness(0, 1, 0, 0);
            return view;
        }

        // Direction A uses a muted, single-hue icon treatment for metrics;
        // warnings are expressed by the tile color, not by the icon.
        internal static Color MetricIconColorByKey(string iconKey)
        {
            return TextSecondary;
        }
    }
}
