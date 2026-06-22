using System;
using System.IO;
using System.Reflection;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace PowerAudioManager
{
    // Embedded-resource loading shared across the app: the bundled HarmonyOS
    // Sans SC font (used by both WPF windows and the WinForms tray menu) and
    // the png/ico images. Kept here as pure statics so neither MainWindow nor
    // the dialogs need to carry font-extraction / resource-stream plumbing.
    internal static class AppResources
    {
        const string FontFileName = "HarmonyOS_Sans_SC_Regular.ttf";

        static readonly FontFamily _appFont = LoadAppFont();

        // WPF font family for the HarmonyOS Sans SC font (falls back to
        // Microsoft YaHei UI if the embedded resource can't be loaded).
        public static FontFamily AppFont { get { return _appFont; } }

        // Emoji / symbol font (system-provided), kept here for a single source.
        public static readonly FontFamily EmojiFont =
            new FontFamily("Segoe UI Symbol, Segoe UI Emoji");

        // ---- Font extraction ---------------------------------------------------

        static string _extractedFontPath;

        // Extract the embedded font (bundled in the exe as a manifest resource) to a
        // temp file so both WPF (FontFamily from file URI) and WinForms
        // (PrivateFontCollection.AddFontFile) can load it. Returns the temp path, or
        // null if the resource is absent.
        static string ExtractEmbeddedFont()
        {
            if (_extractedFontPath != null) return _extractedFontPath;
            try
            {
                var asm = Assembly.GetExecutingAssembly();
                using (var stream = asm.GetManifestResourceStream("PowerAudioManager." + FontFileName))
                {
                    if (stream == null) return null;
                    string tmp = Path.Combine(Path.GetTempPath(), "OneBox_" + FontFileName);
                    using (var fs = new FileStream(tmp, FileMode.Create, FileAccess.Write))
                    {
                        var buf = new byte[8192];
                        int n;
                        while ((n = stream.Read(buf, 0, buf.Length)) > 0) fs.Write(buf, 0, n);
                    }
                    _extractedFontPath = tmp;
                    return tmp;
                }
            }
            catch { return null; }
        }

        // Resolve the font file path: prefer an extracted embedded resource, fall back
        // to a ttf shipped next to the exe. Returns null if none available.
        static string ResolveFontFile()
        {
            string tmp = ExtractEmbeddedFont();
            if (tmp != null && File.Exists(tmp)) return tmp;
            try
            {
                var exeDir = Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location);
                if (!string.IsNullOrEmpty(exeDir))
                {
                    string p = Path.Combine(exeDir, FontFileName);
                    if (File.Exists(p)) return p;
                }
            }
            catch { }
            return null;
        }

        static FontFamily LoadAppFont()
        {
            try
            {
                var ttf = ResolveFontFile();
                if (ttf != null)
                {
                    string dir = Path.GetDirectoryName(ttf) + Path.DirectorySeparatorChar;
                    // "#HarmonyOS Sans SC" is the font's internal family name (not the file name).
                    return new FontFamily(new Uri(dir), "./#HarmonyOS Sans SC");
                }
            }
            catch { }
            return new FontFamily("Microsoft YaHei UI");
        }

        // WinForms (tray menu) counterpart of AppFont: load the same HarmonyOS ttf into a
        // System.Drawing.Font so the right-click menu matches the window font.
        static System.Drawing.Font _trayFont;
        public static System.Drawing.Font TrayFont()
        {
            if (_trayFont != null) return _trayFont;
            try
            {
                var ttf = ResolveFontFile();
                if (ttf != null)
                {
                    var pfc = new System.Drawing.Text.PrivateFontCollection();
                    pfc.AddFontFile(ttf);
                    _trayFont = new System.Drawing.Font(pfc.Families[0], 9f, System.Drawing.FontStyle.Regular, System.Drawing.GraphicsUnit.Point);
                    return _trayFont;
                }
            }
            catch { }
            _trayFont = new System.Drawing.Font("Microsoft YaHei UI", 9f);
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
    }
}
