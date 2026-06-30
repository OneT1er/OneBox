using System;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace PowerAudioManager
{
    // 图片翻译结果窗口：展示 paste=1 整图贴合图，提供复制译文/选择复制按钮。
    // 图片在可滚动可缩放区域中显示。
    internal static class ImageTranslateWindow
    {
        public static void Show(Window owner, byte[] pasteImagePng, string dst, string error)
        {
            var body = new Grid { Margin = new Thickness(0) };
            body.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
            body.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

            var fg = new SolidColorBrush(Color.FromRgb(190, 188, 220));

            if (error != null)
            {
                body.Children.Add(new TextBlock
                {
                    Text = error,
                    Foreground = new SolidColorBrush(Color.FromRgb(240, 170, 170)),
                    FontSize = 12, TextWrapping = TextWrapping.Wrap,
                    Margin = new Thickness(16), VerticalAlignment = VerticalAlignment.Center
                });
                var errDlg = OneBoxWindow.Create(owner, "图片翻译", 420, 200, body, false);
                errDlg.ShowDialog();
                return;
            }

            var scroller = new ScrollViewer
            {
                VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                HorizontalScrollBarVisibility = ScrollBarVisibility.Auto,
                Background = new SolidColorBrush(Color.FromRgb(24, 22, 36)),
                Margin = new Thickness(8)
            };
            Image img = null;
            if (pasteImagePng != null)
            {
                var bmp = new BitmapImage();
                bmp.BeginInit();
                bmp.CacheOption = BitmapCacheOption.OnLoad;
                bmp.StreamSource = new MemoryStream(pasteImagePng);
                bmp.EndInit();
                bmp.Freeze();
                img = new Image
                {
                    Source = bmp,
                    Stretch = Stretch.None,
                    HorizontalAlignment = HorizontalAlignment.Left,
                    VerticalAlignment = VerticalAlignment.Top
                };
                scroller.Content = img;
            }
            Grid.SetRow(scroller, 0);
            body.Children.Add(scroller);

            var bar = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(8) };
            var copyBtn = new Button { Content = "复制译文", Height = 28, FontSize = 12, Margin = new Thickness(0, 0, 8, 0), Padding = new Thickness(12, 0, 12, 0) };
            AppResources.StyleDialogButton(copyBtn, true);
            var selectBtn = new Button { Content = "选择复制", Height = 28, FontSize = 12, Margin = new Thickness(0, 0, 8, 0), Padding = new Thickness(12, 0, 12, 0) };
            AppResources.StyleDialogButton(selectBtn, false);
            var closeBtn = new Button { Content = "关闭", Height = 28, FontSize = 12, Padding = new Thickness(12, 0, 12, 0) };
            AppResources.StyleDialogButton(closeBtn, false);
            bar.Children.Add(copyBtn); bar.Children.Add(selectBtn); bar.Children.Add(closeBtn);
            Grid.SetRow(bar, 1);
            body.Children.Add(bar);

            // 按图片尺寸计算窗口大小，不超过工作区。
            double w = 600, h = 460;
            if (pasteImagePng != null)
            {
                using (var ms = new MemoryStream(pasteImagePng))
                {
                    var dec = BitmapDecoder.Create(ms, BitmapCreateOptions.IgnoreColorProfile, BitmapCacheOption.None);
                    if (dec.Frames.Count > 0)
                    {
                        var f = dec.Frames[0];
                        w = Math.Min(f.PixelWidth + 40, SystemParameters.WorkArea.Width * 0.8);
                        h = Math.Min(f.PixelHeight + 100, SystemParameters.WorkArea.Height * 0.8);
                        w = Math.Max(w, 360); h = Math.Max(h, 240);
                    }
                }
            }

            var dlg = OneBoxWindow.Create(owner, "图片翻译", w, h, body, true);

            copyBtn.Click += (s, e) =>
            {
                try
                {
                    if (!string.IsNullOrEmpty(dst))
                    {
                        System.Windows.Forms.Clipboard.SetText(dst);
                        copyBtn.Content = "已复制";
                    }
                }
                catch { }
            };
            selectBtn.Click += (s, e) =>
            {
                // 弹出一个文本框以便用户选择复制部分译文。
                if (string.IsNullOrEmpty(dst)) return;
                var tb = new TextBox
                {
                    Text = dst,
                    IsReadOnly = false,
                    TextWrapping = TextWrapping.Wrap,
                    AcceptsReturn = true,
                    VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                    FontSize = 12,
                    Background = new SolidColorBrush(Color.FromRgb(24, 22, 36)),
                    Foreground = new SolidColorBrush(Color.FromRgb(220, 218, 245)),
                    BorderBrush = new SolidColorBrush(Color.FromRgb(80, 75, 120)),
                    Margin = new Thickness(8),
                    Height = 160
                };
                var sel = OneBoxWindow.Create(dlg, "选择复制译文", 420, 240, tb, false);
                tb.SelectAll();
                sel.ShowDialog();
            };
            closeBtn.Click += (s, e) => dlg.Close();

            // Ctrl+滚轮缩放图片。
            if (img != null)
            {
                scroller.PreviewMouseWheel += (sender, e) =>
                {
                    if ((Keyboard.Modifiers & ModifierKeys.Control) != 0)
                    {
                        double scale = e.Delta > 0 ? 1.1 : 0.9;
                        var cur = img.LayoutTransform as ScaleTransform ?? new ScaleTransform(1, 1);
                        img.LayoutTransform = new ScaleTransform(cur.ScaleX * scale, cur.ScaleY * scale);
                        e.Handled = true;
                    }
                };
            }

            dlg.ShowDialog();
        }
    }
}
