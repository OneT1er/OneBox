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
                var dir = Path.GetDirectoryName(Environment.ProcessPath);
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

        // ---- Dialog button styling (shared) ------------------------------------
        // Stamps MaterialDesign's flat button style onto a dialog button. The
        // standalone windows (设置/翻译/剪贴板历史/快捷键) read as one design
        // language with the floating window.
        //
        // primary = true  → 紫影 fill + white text (call-to-action, e.g. 确定/翻译)
        // primary = false → transparent flat button, dim text
        static readonly Color DDim = Color.FromRgb(190, 188, 220);
        static readonly Color DAccent = Color.FromRgb(142, 140, 216);

        public static void StyleDialogButton(Button btn, bool primary)
        {
            // MaterialDesign flat button: transparent fill + hover ripple, no
            // elevation shadow (keeps the dialog's flat card look). The primary
            // call-to-action gets a 紫影 fill so it stands out; secondary buttons
            // stay transparent and rely on the ripple for affordance.
            var style = Application.Current.TryFindResource("MaterialDesignFlatButton") as Style;
            if (style != null) btn.Style = style;
            if (primary)
            {
                btn.Background = new SolidColorBrush(DAccent);
                btn.Foreground = Brushes.White;
                btn.FontWeight = FontWeights.SemiBold;
            }
            else
            {
                btn.Foreground = new SolidColorBrush(DDim);
            }
        }

        // ---- Dark TabControl styling (shared) ----------------------------------
        // Now a no-op: MaterialDesign's implicit TabControl/TabItem styles (from the
        // global MaterialDesign3 defaults) already render the dark 紫影 tab chrome.
        // Kept as a stub so existing call sites compile unchanged during the migration.
        public static void StyleDarkTabControl(TabControl tc)
        {
            tc.Background = Brushes.Transparent;
            tc.BorderBrush = Brushes.Transparent;
            tc.Padding = new Thickness(0);
        }

        // Now a no-op: MaterialDesign's implicit ComboBox style (from the global
        // MaterialDesign3 defaults) renders the dark 紫影 combo + dropdown already.
        // Kept as a stub so existing call sites compile unchanged during the migration.
        public static void StyleDarkComboBox(ComboBox cb)
        {
        }

    }
}
