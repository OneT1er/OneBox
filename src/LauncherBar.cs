using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;

namespace PowerAudioManager
{
    // Quick-launch bar: a row of 4 slots. Each slot holds an exe / shortcut;
    // click to launch, right-click to clear, drag-and-drop an exe or .lnk to
    // assign. Paths persist in the registry (Launcher.Paths, '|'-separated).
    // .lnk targets are resolved via WScript.Shell COM late-binding.
    //
    // Extracted from MainWindow: it owns no window state — the host passes a
    // requestRebuild callback so assigning/clearing a slot can rebuild the UI
    // (which lives in MainWindow).
    internal static class LauncherBar
    {
        const int LauncherSlots = 8;
        const string LauncherPrefKey = "Launcher.Paths";

        // Build the launcher section into contentPanel. requestRebuild is
        // invoked when a slot's path changes (so the host re-renders icons).
        public static void Build(StackPanel contentPanel, Action requestRebuild)
        {
            if (contentPanel.Children.Count > 0) contentPanel.Children.Add(MainWindow.MakeDivider());
            var header = new TextBlock {
                Foreground = new SolidColorBrush(MainWindow.AccentColor), FontSize = 12,
                FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 0, 0, 6) };
            header.Inlines.Add(new Run("🚀") { FontFamily = AppResources.EmojiFont });
            header.Inlines.Add(new Run(" 快捷启动"));
            contentPanel.Children.Add(header);
            var row = new WrapPanel { Orientation = Orientation.Horizontal };
            var paths = LoadLauncherPaths();
            for (int i = 0; i < LauncherSlots; i++)
            {
                string p = i < paths.Count ? paths[i] : null;
                row.Children.Add(MakeLauncherSlot(i, p, paths, requestRebuild));
            }
            contentPanel.Children.Add(row);
        }

        static List<string> LoadLauncherPaths()
        {
            var list = new List<string>();
            try
            {
                using (var k = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(@"Software\PowerAudioManager\App"))
                {
                    if (k != null)
                    {
                        var s = k.GetValue(LauncherPrefKey) as string;
                        if (!string.IsNullOrEmpty(s))
                            foreach (var p in s.Split('|')) if (p.Length > 0) list.Add(p);
                    }
                }
            }
            catch { }
            return list;
        }

        static void SaveLauncherPaths(List<string> paths)
        {
            try
            {
                using (var k = Microsoft.Win32.Registry.CurrentUser.CreateSubKey(@"Software\PowerAudioManager\App"))
                {
                    var sb = new StringBuilder();
                    for (int i = 0; i < paths.Count; i++) { if (i > 0) sb.Append('|'); sb.Append(paths[i]); }
                    k.SetValue(LauncherPrefKey, sb.ToString());
                }
            }
            catch { }
        }

        // Extract a small icon from an exe/dll/lnk for the launcher slot.
        static ImageSource ExtractIcon(string path)
        {
            try
            {
                var ico = System.Drawing.Icon.ExtractAssociatedIcon(path);
                if (ico != null)
                {
                    var bmp = System.Windows.Interop.Imaging.CreateBitmapSourceFromHIcon(
                        ico.Handle, Int32Rect.Empty,
                        System.Windows.Media.Imaging.BitmapSizeOptions.FromEmptyOptions());
                    bmp.Freeze();
                    return bmp;
                }
            }
            catch { }
            return null;
        }

        static Button MakeLauncherSlot(int index, string path, List<string> paths, Action requestRebuild)
        {
            var btn = new Button {
                Width = 44, Height = 44,
                Margin = new Thickness(0, 0, 6, 6),
                Cursor = Cursors.Hand,
                Background = new SolidColorBrush(MainWindow.CardColor),
                BorderBrush = new SolidColorBrush(MainWindow.BorderColor),
                ToolTip = string.IsNullOrEmpty(path) ? "点击或拖入程序（exe / 快捷方式）" : path,
                AllowDrop = true
            };
            if (!string.IsNullOrEmpty(path))
            {
                var img = ExtractIcon(path);
                if (img != null)
                    btn.Content = new System.Windows.Controls.Image { Source = img, Width = 24, Height = 24 };
                else if (path.StartsWith("http://", StringComparison.OrdinalIgnoreCase) || path.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
                {
                    btn.Content = "🌐";
                    btn.FontSize = 18;
                }
                else if (System.IO.Directory.Exists(path))
                {
                    btn.Content = "📁";
                    btn.FontSize = 18;
                }
                else
                {
                    btn.Content = "•";
                    btn.FontSize = 18;
                }
            }
            else
            {
                btn.Content = "+";
                btn.FontSize = 18;
                btn.Foreground = new SolidColorBrush(MainWindow.TextSecondary);
            }
            btn.Click += (s, e) =>
            {
                if (string.IsNullOrEmpty(path))
                {
                    // Pick an executable
                    var dlg = new Microsoft.Win32.OpenFileDialog {
                        Filter = "程序|*.exe;*.lnk|所有文件|*.*",
                        Title = "选择要添加的程序" };
                    if (dlg.ShowDialog() == true)
                        SetLauncherSlotPath(index, dlg.FileName, paths, requestRebuild);
                }
                else
                {
                    try { Process.Start(path); }
                    catch (Exception ex) { AppLog.Log("Launch " + path, ex); }
                }
            };
            btn.MouseRightButtonUp += (s, e) =>
            {
                // Right-click clears the slot
                if (!string.IsNullOrEmpty(path))
                {
                    if (index < paths.Count) paths[index] = "";
                    SaveLauncherPaths(paths);
                    requestRebuild();
                    e.Handled = true;
                }
            };
            // Drag-and-drop: drop an exe / shortcut / folder / URL onto the slot to assign it.
            btn.DragEnter += (s, e) =>
            {
                if (e.Data.GetDataPresent(DataFormats.FileDrop) || e.Data.GetDataPresent(DataFormats.Text) || e.Data.GetDataPresent(DataFormats.StringFormat))
                {
                    e.Effects = DragDropEffects.Copy;
                    btn.BorderBrush = new SolidColorBrush(MainWindow.AccentColor);
                    btn.BorderThickness = new Thickness(2);
                }
                else { e.Effects = DragDropEffects.None; }
                e.Handled = true;
            };
            btn.DragLeave += (s, e) =>
            {
                btn.BorderBrush = new SolidColorBrush(MainWindow.BorderColor);
                btn.BorderThickness = new Thickness(1);
                e.Handled = true;
            };
            btn.Drop += (s, e) =>
            {
                btn.BorderBrush = new SolidColorBrush(MainWindow.BorderColor);
                btn.BorderThickness = new Thickness(1);
                if (e.Data.GetDataPresent(DataFormats.FileDrop))
                {
                    var files = (string[])e.Data.GetData(DataFormats.FileDrop);
                    if (files != null && files.Length > 0)
                    {
                        string dropped = files[0];
                        // Resolve .lnk shortcuts to their target so the icon/launch works.
                        string resolved = ResolveShortcut(dropped);
                        SetLauncherSlotPath(index, resolved, paths, requestRebuild);
                    }
                }
                else if (e.Data.GetDataPresent(DataFormats.Text))
                {
                    string text = e.Data.GetData(DataFormats.Text) as string;
                    if (!string.IsNullOrEmpty(text) && (text.StartsWith("http://", StringComparison.OrdinalIgnoreCase) || text.StartsWith("https://", StringComparison.OrdinalIgnoreCase)))
                    {
                        SetLauncherSlotPath(index, text, paths, requestRebuild);
                    }
                    else if (!string.IsNullOrEmpty(text) && System.IO.Directory.Exists(text))
                    {
                        SetLauncherSlotPath(index, text, paths, requestRebuild);
                    }
                }
                else if (e.Data.GetDataPresent(DataFormats.StringFormat))
                {
                    string text = e.Data.GetData(DataFormats.StringFormat) as string;
                    if (!string.IsNullOrEmpty(text) && (text.StartsWith("http://", StringComparison.OrdinalIgnoreCase) || text.StartsWith("https://", StringComparison.OrdinalIgnoreCase)))
                    {
                        SetLauncherSlotPath(index, text, paths, requestRebuild);
                    }
                }
                e.Handled = true;
            };
            return btn;
        }

        // Assign a path to a launcher slot (clamps the list, saves, rebuilds).
        static void SetLauncherSlotPath(int index, string path, List<string> paths, Action requestRebuild)
        {
            if (string.IsNullOrEmpty(path)) return;
            while (paths.Count <= index) paths.Add("");
            if (paths.Count > index) paths[index] = path; else paths.Add(path);
            SaveLauncherPaths(paths);
            requestRebuild();
        }

        // Resolve a .lnk shortcut to its target path. Falls back to the original path
        // if it's not a shortcut or resolution fails. Uses late binding via reflection
        // (no dynamic / Microsoft.CSharp dependency).
        static string ResolveShortcut(string path)
        {
            if (string.IsNullOrEmpty(path)) return path;
            try
            {
                if (!path.EndsWith(".lnk", StringComparison.OrdinalIgnoreCase)) return path;
                // WScript.Shell.CreateShortcut parses .lnk files via COM late-binding.
                Type shellType = Type.GetTypeFromProgID("WScript.Shell");
                if (shellType == null) return path;
                object shell = Activator.CreateInstance(shellType);
                try
                {
                    object sc = shellType.InvokeMember("CreateShortcut",
                        BindingFlags.InvokeMethod, null, shell, new object[] { path });
                    if (sc == null) return path;
                    try
                    {
                        object target = sc.GetType().InvokeMember("TargetPath",
                            BindingFlags.GetProperty, null, sc, null);
                        string t = target as string;
                        if (!string.IsNullOrEmpty(t)) return t;
                    }
                    finally { try { Marshal.ReleaseComObject(sc); } catch { } }
                }
                finally { try { Marshal.ReleaseComObject(shell); } catch { } }
            }
            catch (Exception ex) { AppLog.Log("ResolveShortcut " + path, ex); }
            return path;
        }
    }
}
