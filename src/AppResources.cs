using System;
using System.IO;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace PowerAudioManager
{
    // Shared resource access: the app font family (user-selectable from the
    // system's installed fonts) and bundled images. Fonts are NO LONGER packed
    // into the exe — we use whatever the user picks (default Microsoft YaHei UI).
    internal static class AppResources
    {
        const string DefaultFontName = "Microsoft YaHei UI";
        const string FontPrefKey = "App.FontFamily";

        // WPF font family for the user-selected system font (falls back to
        // Microsoft YaHei UI if the saved name is missing or invalid).
        static FontFamily _cachedAppFont;
        static string _cachedFontName;

        public static FontFamily AppFont
        {
            get
            {
                var name = AppPrefs.GetString(FontPrefKey, DefaultFontName);
                if (_cachedAppFont == null || _cachedFontName != name)
                {
                    _cachedAppFont = ResolveFont(name);
                    _cachedFontName = name;
                }
                return _cachedAppFont;
            }
        }

        // Re-read the font (called after the user changes it in settings) and
        // returns the new family so the host can apply it to the window.
        public static FontFamily ReloadFont()
        {
            _cachedAppFont = null;
            _cachedFontName = null;
            return AppFont;
        }

        // Build a FontFamily, falling back to the default if the name doesn't
        // resolve to an installed family.
        static FontFamily ResolveFont(string name)
        {
            try
            {
                if (!string.IsNullOrWhiteSpace(name))
                {
                    var ff = new FontFamily(name);
                    // Verify the family is actually installed.
                    foreach (var f in Fonts.SystemFontFamilies)
                        if (string.Equals(f.Source, name, StringComparison.OrdinalIgnoreCase))
                            return ff;
                }
            }
            catch { }
            return new FontFamily(DefaultFontName);
        }

        // Emoji / symbol font (system-provided).
        public static readonly FontFamily EmojiFont =
            new FontFamily("Segoe UI Symbol, Segoe UI Emoji");

        // WinForms (tray menu) counterpart of AppFont: a System.Drawing.Font
        // built from the same family name so the right-click menu matches.
        static System.Drawing.Font _trayFont;
        static string _trayFontName;
        public static System.Drawing.Font TrayFont()
        {
            var name = AppPrefs.GetString(FontPrefKey, DefaultFontName);
            if (_trayFont != null && _trayFontName == name) return _trayFont;
            // Dispose the previous font when the family changes.
            if (_trayFont != null) { try { _trayFont.Dispose(); } catch { } _trayFont = null; }
            try
            {
                // Verify the family is installed before handing it to WinForms;
                // an unknown name silently falls back to a default there too,
                // but we keep the behaviour explicit.
                bool installed = false;
                using (var col = new System.Drawing.Text.InstalledFontCollection())
                    foreach (var f in col.Families)
                        if (string.Equals(f.Name, name, StringComparison.OrdinalIgnoreCase))
                        { installed = true; break; }
                _trayFont = new System.Drawing.Font(installed ? name : DefaultFontName, 9f);
            }
            catch { _trayFont = new System.Drawing.Font(DefaultFontName, 9f); }
            _trayFontName = name;
            return _trayFont;
        }

        // ---- Image resources ---------------------------------------------------

        // Load a bundled image (png/ico) by file name: prefer the embedded manifest
        // resource (so no external file is needed), fall back to a file next to the
        // exe. Returns a frozen BitmapSource, or null if unavailable.
        public static BitmapImage LoadAppImage(string fileName)
        {
            try
            {
                var asm = Assembly.GetExecutingAssembly();
                string resName = "PowerAudioManager." + fileName;
                using (var stream = asm.GetManifestResourceStream(resName))
                {
                    if (stream != null)
                    {
                        var bmp = new BitmapImage();
                        bmp.BeginInit();
                        bmp.CacheOption = BitmapCacheOption.OnLoad;
                        bmp.StreamSource = stream;
                        bmp.EndInit();
                        bmp.Freeze();
                        return bmp;
                    }
                }
                // Fallback: external file next to the exe.
                var dir = Path.GetDirectoryName(asm.Location);
                var path = Path.Combine(dir, fileName);
                if (File.Exists(path))
                {
                    var bmp = new BitmapImage();
                    bmp.BeginInit();
                    bmp.UriSource = new Uri(path);
                    bmp.CacheOption = BitmapCacheOption.OnLoad;
                    bmp.EndInit();
                    bmp.Freeze();
                    return bmp;
                }
            }
            catch { }
            return null;
        }

        // ---- Dark ComboBox styling (shared) ------------------------------------
        // The default WPF ComboBox template is light, which makes light text
        // unreadable and clashes with the dark OneBox UI. This swaps in a dark
        // control template (transparent selection area so the combo's own
        // Background shows through, a ▾ glyph, dark rounded dropdown) plus the
        // SystemColor overrides and item-container style needed for the popup.

        static readonly Color ComboBg = Color.FromRgb(42, 39, 60);
        static readonly Color ComboDropBg = Color.FromRgb(34, 32, 50);
        static readonly Color ComboBorder = Color.FromRgb(80, 75, 120);
        static readonly Color ComboHover = Color.FromRgb(58, 54, 84);
        static readonly Color ComboAccent = Color.FromRgb(110, 105, 200);
        static readonly Color ComboText = Color.FromRgb(220, 218, 245);
        static readonly Color ComboDim = Color.FromRgb(190, 188, 220);

        public static void StyleDarkComboBox(ComboBox cb)
        {
            var lightText = new SolidColorBrush(ComboText);

            // Selection area + popup text colour overrides.
            var tbStyle = new Style(typeof(TextBlock));
            tbStyle.Setters.Add(new Setter(TextBlock.ForegroundProperty, lightText));
            cb.Resources.Add(typeof(TextBlock), tbStyle);
            cb.Resources.Add(SystemColors.WindowBrushKey, new SolidColorBrush(ComboDropBg));
            cb.Resources.Add(SystemColors.WindowTextBrushKey, lightText);
            cb.Resources.Add(SystemColors.HighlightBrushKey, new SolidColorBrush(ComboAccent));
            cb.Resources.Add(SystemColors.HighlightTextBrushKey, Brushes.White);
            cb.Resources.Add(SystemColors.ControlBrushKey, new SolidColorBrush(ComboBg));
            cb.Resources.Add(SystemColors.ControlTextBrushKey, lightText);

            cb.Background = new SolidColorBrush(ComboBg);
            cb.Foreground = lightText;
            cb.BorderBrush = new SolidColorBrush(ComboBorder);
            cb.Template = DarkComboBoxTemplate();

            // Dark item container with a purple hover.
            var itemStyle = new Style(typeof(ComboBoxItem));
            itemStyle.Setters.Add(new Setter(ComboBoxItem.BackgroundProperty, new SolidColorBrush(ComboDropBg)));
            itemStyle.Setters.Add(new Setter(ComboBoxItem.ForegroundProperty, lightText));
            itemStyle.Setters.Add(new Setter(ComboBoxItem.PaddingProperty, new Thickness(8, 4, 8, 4)));
            itemStyle.Setters.Add(new Setter(ComboBoxItem.BorderBrushProperty, Brushes.Transparent));
            var hover = new Trigger { Property = ComboBoxItem.IsHighlightedProperty, Value = true };
            hover.Setters.Add(new Setter(ComboBoxItem.BackgroundProperty, new SolidColorBrush(ComboHover)));
            hover.Setters.Add(new Setter(ComboBoxItem.ForegroundProperty, Brushes.White));
            itemStyle.Triggers.Add(hover);
            cb.ItemContainerStyle = itemStyle;
        }

        static ControlTemplate DarkComboBoxTemplate()
        {
            var template = new ControlTemplate(typeof(ComboBox));

            // Root: grid holding the content presenter (selected item) + toggle button + arrow.
            var root = new FrameworkElementFactory(typeof(Grid));

            // Background fill behind the selection area (the combo's own Background
            // doesn't render with the default template, so we paint it here).
            var bg = new FrameworkElementFactory(typeof(Border));
            bg.SetValue(Border.BackgroundProperty, new SolidColorBrush(ComboBg));
            bg.SetValue(Border.BorderBrushProperty, new SolidColorBrush(ComboBorder));
            bg.SetValue(Border.BorderThicknessProperty, new Thickness(1));
            bg.SetValue(Border.CornerRadiusProperty, new CornerRadius(4));
            root.AppendChild(bg);

            // Content presenter for the selected item.
            var cp = new FrameworkElementFactory(typeof(ContentPresenter));
            cp.SetValue(ContentPresenter.ContentSourceProperty, "SelectionBoxItem");
            cp.SetValue(ContentPresenter.HorizontalAlignmentProperty, HorizontalAlignment.Stretch);
            cp.SetValue(ContentPresenter.VerticalAlignmentProperty, VerticalAlignment.Center);
            cp.SetValue(ContentPresenter.MarginProperty, new Thickness(8, 0, 22, 0));
            cp.SetValue(ContentPresenter.SnapsToDevicePixelsProperty, true);
            root.AppendChild(cp);

            // Toggle button overlay capturing clicks for the whole combo area.
            var toggle = new FrameworkElementFactory(typeof(ToggleButton));
            toggle.SetValue(ToggleButton.FocusableProperty, false);
            toggle.SetValue(ToggleButton.IsTabStopProperty, false);
            toggle.SetValue(ToggleButton.HorizontalAlignmentProperty, HorizontalAlignment.Stretch);
            toggle.SetValue(ToggleButton.VerticalAlignmentProperty, VerticalAlignment.Stretch);
            var isOpenCheckBinding = new System.Windows.Data.Binding("IsDropDownOpen") {
                Mode = System.Windows.Data.BindingMode.TwoWay,
                RelativeSource = new System.Windows.Data.RelativeSource(System.Windows.Data.RelativeSourceMode.TemplatedParent)
            };
            toggle.SetBinding(ToggleButton.IsCheckedProperty, isOpenCheckBinding);
            var tbTemplate = new ControlTemplate(typeof(ToggleButton));
            var tbRoot = new FrameworkElementFactory(typeof(Border));
            tbRoot.SetValue(Border.BackgroundProperty, Brushes.Transparent);
            tbRoot.SetValue(Border.HorizontalAlignmentProperty, HorizontalAlignment.Stretch);
            tbRoot.SetValue(Border.VerticalAlignmentProperty, VerticalAlignment.Stretch);
            tbTemplate.VisualTree = tbRoot;
            toggle.SetValue(ToggleButton.TemplateProperty, tbTemplate);
            root.AppendChild(toggle);

            // Dropdown arrow glyph.
            var arrow = new FrameworkElementFactory(typeof(TextBlock));
            arrow.SetValue(TextBlock.TextProperty, "▾");
            arrow.SetValue(TextBlock.FontSizeProperty, 10.0);
            arrow.SetValue(TextBlock.ForegroundProperty, new SolidColorBrush(ComboDim));
            arrow.SetValue(TextBlock.HorizontalAlignmentProperty, HorizontalAlignment.Right);
            arrow.SetValue(TextBlock.VerticalAlignmentProperty, VerticalAlignment.Center);
            arrow.SetValue(TextBlock.MarginProperty, new Thickness(0, 0, 8, 0));
            arrow.SetValue(TextBlock.IsHitTestVisibleProperty, false);
            root.AppendChild(arrow);

            // Popup hosting the items.
            var popup = new FrameworkElementFactory(typeof(Popup));
            popup.SetValue(Popup.NameProperty, "PART_Popup");
            popup.SetValue(Popup.AllowsTransparencyProperty, true);
            popup.SetValue(Popup.PlacementProperty, PlacementMode.Bottom);
            popup.SetValue(Popup.PopupAnimationProperty, PopupAnimation.None);
            var isOpenBinding = new System.Windows.Data.Binding("IsDropDownOpen") {
                RelativeSource = new System.Windows.Data.RelativeSource(System.Windows.Data.RelativeSourceMode.TemplatedParent)
            };
            popup.SetBinding(Popup.IsOpenProperty, isOpenBinding);
            popup.SetValue(Popup.FocusableProperty, false);

            var dropBorder = new FrameworkElementFactory(typeof(Border));
            dropBorder.SetValue(Border.BackgroundProperty, new SolidColorBrush(ComboDropBg));
            dropBorder.SetValue(Border.BorderBrushProperty, new SolidColorBrush(ComboBorder));
            dropBorder.SetValue(Border.BorderThicknessProperty, new Thickness(1));
            dropBorder.SetValue(Border.CornerRadiusProperty, new CornerRadius(6));

            var scroller = new FrameworkElementFactory(typeof(ScrollViewer));
            scroller.SetValue(ScrollViewer.VerticalScrollBarVisibilityProperty, ScrollBarVisibility.Auto);
            scroller.SetValue(ScrollViewer.MaxHeightProperty, 300.0);
            var itemsHost = new FrameworkElementFactory(typeof(ItemsPresenter));
            itemsHost.SetValue(KeyboardNavigation.DirectionalNavigationProperty, KeyboardNavigationMode.Cycle);
            scroller.AppendChild(itemsHost);
            dropBorder.AppendChild(scroller);
            popup.AppendChild(dropBorder);
            root.AppendChild(popup);

            template.VisualTree = root;
            return template;
        }
    }
}
