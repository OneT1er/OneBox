using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Diagnostics;

namespace PowerAudioManager
{
    // Standalone gallery window: a thumbnail grid of all screenshots under the
    // root directory (newest first). Click a thumbnail to reveal it in Explorer.
    // Opened from the floating window's 图库 button.
    internal static class ScreenshotGallery
    {
        const int ThumbSize = 120;

        public static void Show(Window owner)
        {
            var outer = new DockPanel { Margin = new Thickness(12) };

            var header = new TextBlock
            {
                Text = "截图图库（点击在资源管理器中定位 · 右键打开）",
                Foreground = Brushes.White, FontSize = 12, Margin = new Thickness(0, 0, 0, 8)
            };
            DockPanel.SetDock(header, Dock.Top);
            outer.Children.Add(header);

            var openRootBtn = new Button
            {
                Content = "打开截图文件夹", Width = 120, Height = 24, FontSize = 11, Margin = new Thickness(0, 0, 0, 8)
            };
            AppResources.StyleDialogButton(openRootBtn, false);
            DockPanel.SetDock(openRootBtn, Dock.Bottom);
            outer.Children.Add(openRootBtn);

            var scroller = new ScrollViewer { VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
            var wrap = new WrapPanel();
            scroller.Content = wrap;

            var dlg = OneBoxWindow.Create(owner, "图库", 600, 480, outer, true);
            openRootBtn.Click += (s, e) => { try { Process.Start("explorer.exe", "\"" + ScreenshotService.RootDir() + "\""); } catch { } };

            // Load thumbnails (newest first) on a background thread so the dialog
            // opens instantly; images stream in as they decode.
            ThreadPool.QueueUserWorkItem(_ =>
            {
                var files = ScreenshotService.GetRecent(int.MaxValue);
                foreach (var f in files)
                {
                    var path = f;
                    var thumb = ScreenshotService.LoadThumbnail(path, ThumbSize * 2, ThumbSize * 2);
                    if (thumb == null) continue;
                    dlg.Dispatcher.BeginInvoke(new Action(() =>
                    {
                        var img = new System.Windows.Controls.Image
                        {
                            Source = thumb,
                            Width = ThumbSize, Height = ThumbSize,
                            Stretch = Stretch.UniformToFill,
                            Margin = new Thickness(4),
                            Cursor = Cursors.Hand,
                            ToolTip = Path.GetFileName(path)
                        };
                        img.MouseLeftButtonDown += (s, e) =>
                        {
                            try { Process.Start("explorer.exe", "/select,\"" + path + "\""); } catch { }
                        };
                        img.MouseRightButtonUp += (s, e) =>
                        {
                            try { Process.Start("explorer.exe", "\"" + Path.GetDirectoryName(path) + "\""); } catch { }
                            e.Handled = true;
                        };
                        wrap.Children.Add(img);
                    }));
                }
                dlg.Dispatcher.BeginInvoke(new Action(() =>
                {
                    if (wrap.Children.Count == 0)
                        wrap.Children.Add(new TextBlock
                        {
                            Text = "（暂无截图）",
                            Foreground = new SolidColorBrush(Color.FromRgb(190, 188, 220)),
                            FontSize = 11, Margin = new Thickness(0, 8, 0, 0)
                        });
                }));
            });

            dlg.ShowDialog();
        }
    }
}
