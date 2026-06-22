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

        // ---- Dialog button styling (shared, Material-ish) ----------------------
        // Gives the standalone windows (设置/翻译/剪贴板历史/快捷键) the same
        // rounded dark button look as the floating window, so the app reads as one
        // design language instead of default WPF buttons in dialogs.
        //
        // primary = true  → accent fill + white text (call-to-action, e.g. 确定/翻译)
        // primary = false → card fill + border, hover lifts to the hover tier
        static readonly Color DBg = Color.FromRgb(42, 39, 60);
        static readonly Color DHover = Color.FromRgb(58, 54, 84);
        static readonly Color DBorder = Color.FromRgb(80, 75, 120);
        static readonly Color DText = Color.FromRgb(220, 218, 245);
        static readonly Color DDim = Color.FromRgb(190, 188, 220);
        static readonly Color DAccent = Color.FromRgb(142, 140, 216);
        static readonly Color DAccentHover = Color.FromRgb(126, 122, 210);

        public static void StyleDialogButton(Button btn, bool primary)
        {
            btn.Template = DialogButtonTemplate();
            if (primary)
            {
                btn.Background = new SolidColorBrush(DAccent);
                btn.Foreground = Brushes.White;
                btn.FontWeight = FontWeights.SemiBold;
                btn.BorderBrush = new SolidColorBrush(DAccent);
                btn.BorderThickness = new Thickness(0);
                btn.MouseEnter += (s, e) => AnimateBg(btn, DAccentHover);
                btn.MouseLeave += (s, e) => AnimateBg(btn, DAccent);
            }
            else
            {
                btn.Background = new SolidColorBrush(DBg);
                btn.Foreground = new SolidColorBrush(DDim);
                btn.BorderBrush = new SolidColorBrush(DBorder);
                btn.BorderThickness = new Thickness(1);
                btn.MouseEnter += (s, e) => AnimateBg(btn, DHover);
                btn.MouseLeave += (s, e) => AnimateBg(btn, DBg);
            }
        }

        static void AnimateBg(Button btn, Color to)
        {
            var b = btn.Background as SolidColorBrush;
            if (b == null) { btn.Background = new SolidColorBrush(to); return; }
            b.BeginAnimation(SolidColorBrush.ColorProperty,
                new System.Windows.Media.Animation.ColorAnimation(to, TimeSpan.FromMilliseconds(180)));
        }

        static ControlTemplate DialogButtonTemplate()
        {
            var template = new ControlTemplate(typeof(Button));
            var root = new FrameworkElementFactory(typeof(Border));
            root.SetValue(Border.CornerRadiusProperty, new CornerRadius(8));
            root.SetValue(Border.BackgroundProperty, new TemplateBindingExtension(Border.BackgroundProperty));
            root.SetValue(Border.BorderBrushProperty, new TemplateBindingExtension(Border.BorderBrushProperty));
            root.SetValue(Border.BorderThicknessProperty, new TemplateBindingExtension(Border.BorderThicknessProperty));
            var cp = new FrameworkElementFactory(typeof(ContentPresenter));
            cp.SetValue(ContentPresenter.MarginProperty, new TemplateBindingExtension(Control.PaddingProperty));
            cp.SetValue(ContentPresenter.HorizontalAlignmentProperty, new TemplateBindingExtension(ContentControl.HorizontalContentAlignmentProperty));
            cp.SetValue(ContentPresenter.VerticalAlignmentProperty, VerticalAlignment.Center);
            cp.SetValue(ContentPresenter.RecognizesAccessKeyProperty, true);
            root.AppendChild(cp);
            template.VisualTree = root;
            return template;
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

        // ---- Dark TabControl styling (shared) ----------------------------------
        // Replaces the default (light) TabControl chrome so the 设置 window's
        // 常规/板块/内存/翻译 tabs match the dark OneBox theme: pill-shaped tab
        // headers, accent fill on the selected tab, transparent content area.
        public static void StyleDarkTabControl(TabControl tc)
        {
            tc.Background = Brushes.Transparent;
            tc.BorderBrush = Brushes.Transparent;
            tc.Padding = new Thickness(0);

            // TabItem template: a rounded-top pill, accent-filled when selected.
            const string itemXaml =
@"<ControlTemplate TargetType='TabItem' xmlns='http://schemas.microsoft.com/winfx/2006/xaml/presentation'>
  <Border x:Name='bd' CornerRadius='8,8,0,0' Background='Transparent' BorderBrush='Transparent' BorderThickness='1,1,1,0' Padding='14,6,14,6' Margin='2,0,2,0'>
    <ContentPresenter ContentSource='Header' HorizontalAlignment='Center' VerticalAlignment='Center'/>
  </Border>
  <ControlTemplate.Triggers>
    <Trigger Property='IsSelected' Value='True'>
      <Setter TargetName='bd' Property='Background' Value='#8E8CD8'/>
      <Setter TargetName='bd' Property='BorderBrush' Value='#8E8CD8'/>
      <Setter Property='Foreground' Value='White'/>
      <Setter Property='FontWeight' Value='SemiBold'/>
    </Trigger>
    <Trigger Property='IsMouseOver' Value='True'>
      <Setter TargetName='bd' Property='Background' Value='#3A3654'/>
    </Trigger>
    <MultiTrigger>
      <MultiTrigger.Conditions>
        <Condition Property='IsMouseOver' Value='True'/>
        <Condition Property='IsSelected' Value='True'/>
      </MultiTrigger.Conditions>
      <Setter TargetName='bd' Property='Background' Value='#7E7AD2'/>
    </MultiTrigger>
  </ControlTemplate.Triggers>
</ControlTemplate>";
            var itemStyle = new Style(typeof(TabItem));
            itemStyle.Setters.Add(new Setter(TabItem.ForegroundProperty, new SolidColorBrush(Color.FromRgb(190, 188, 220))));
            itemStyle.Setters.Add(new Setter(TabItem.FontSizeProperty, 13.0));
            itemStyle.Setters.Add(new Setter(TabItem.TemplateProperty, (ControlTemplate)System.Windows.Markup.XamlReader.Parse(itemXaml)));
            tc.Resources.Add(typeof(TabItem), itemStyle);

            // TabControl template: tab strip on top (transparent), content below,
            // with a thin accent underline beneath the selected row.
            const string tcXaml =
@"<ControlTemplate TargetType='TabControl' xmlns='http://schemas.microsoft.com/winfx/2006/xaml/presentation'>
  <Grid>
    <Grid.RowDefinitions>
      <RowDefinition Height='Auto'/>
      <RowDefinition Height='*'/>
    </Grid.RowDefinitions>
    <Grid>
      <Border Background='#222132' CornerRadius='8,8,0,0'/>
      <TabPanel x:Name='HeaderPanel' IsItemsHost='True' Margin='6,6,6,0'/>
    </Grid>
    <Border Grid.Row='1' Background='#1C1A28' BorderBrush='#504F78' BorderThickness='1,0,1,1' CornerRadius='0,0,8,8'>
      <ContentPresenter ContentSource='SelectedContent'/>
    </Border>
  </Grid>
</ControlTemplate>";
            tc.Template = (ControlTemplate)System.Windows.Markup.XamlReader.Parse(tcXaml);
        }

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
