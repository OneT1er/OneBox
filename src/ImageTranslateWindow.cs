using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace PowerAudioManager
{
    // 图片翻译结果窗口：左侧展示 paste=1 整图贴合图（Ctrl+滚轮缩放），右侧按行列出译文，
    // 支持逐段复制、复制全部译文、勾选复制。
    internal static class ImageTranslateWindow
    {
        const double SidePanelWidth = 300;

        public static void Show(Window owner, byte[] pasteImagePng, string dst, string error)
        {
            if (error != null)
            {
                var errBody = new DockPanel { Margin = new Thickness(16, 2, 16, 16), LastChildFill = true };
                var badge = new Border
                {
                    Width = 20, Height = 20, CornerRadius = new CornerRadius(10),
                    Background = UiKit.FrozenBrush(UiKit.DangerSurface),
                    Margin = new Thickness(0, 0, 10, 0), VerticalAlignment = VerticalAlignment.Top,
                    Child = new TextBlock { Text = "!", FontSize = 12, FontWeight = FontWeights.Bold,
                        Foreground = UiKit.FrozenBrush(UiKit.DangerColor),
                        HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center }
                };
                DockPanel.SetDock(badge, Dock.Left);
                errBody.Children.Add(badge);
                errBody.Children.Add(new TextBlock
                {
                    Text = error,
                    Foreground = UiKit.FrozenBrush(UiKit.TextSecondary),
                    FontSize = 12, LineHeight = 18, TextWrapping = TextWrapping.Wrap
                });
                var errDlg = OneBoxWindow.Create(owner, "图片翻译", 420, 200, errBody, false, IconKey.Translate);
                errDlg.SizeToContent = SizeToContent.Height;
                errDlg.ShowDialog();
                return;
            }

            var segments = SplitSegments(dst);

            var body = new Grid { Margin = new Thickness(12, 0, 12, 12) };
            body.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            body.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(12) });
            body.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(SidePanelWidth) });
            body.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
            body.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

            // Left: translated image.
            var scroller = new ScrollViewer
            {
                VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                HorizontalScrollBarVisibility = ScrollBarVisibility.Auto,
                Padding = new Thickness(8)
            };
            scroller.Resources[typeof(System.Windows.Controls.Primitives.ScrollBar)] = ThemeTokens.CreateDarkScrollBarStyle();
            var imageHost = new Border
            {
                CornerRadius = new CornerRadius(10),
                Background = UiKit.FrozenBrush(Color.FromRgb(28, 26, 38)),
                Child = scroller
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
            Grid.SetColumn(imageHost, 0); Grid.SetRow(imageHost, 0);
            body.Children.Add(imageHost);

            // Right: segment list.
            var side = new DockPanel { LastChildFill = true };
            var sideHead = new DockPanel { LastChildFill = true, Margin = new Thickness(0, 0, 0, 8) };
            var copyAll = UiKit.MakeButton("复制全部译文", ButtonVariant.Primary);
            copyAll.IsEnabled = !string.IsNullOrEmpty(dst);
            DockPanel.SetDock(copyAll, Dock.Right);
            sideHead.Children.Add(copyAll);
            sideHead.Children.Add(UiKit.Text("译文 · " + segments.Count + " 段", 12, UiKit.TextPrimary, FontWeights.SemiBold));
            DockPanel.SetDock(sideHead, Dock.Top);
            side.Children.Add(sideHead);

            var listScroll = new ScrollViewer { VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
            listScroll.Resources[typeof(System.Windows.Controls.Primitives.ScrollBar)] = ThemeTokens.CreateDarkScrollBarStyle();
            var list = new StackPanel();
            for (int i = 0; i < segments.Count; i++)
                list.Children.Add(SegmentCard(i + 1, segments[i]));
            if (segments.Count == 0)
                list.Children.Add(UiKit.Text("没有识别到可翻译的文字", 12, UiKit.TextSecondary));
            listScroll.Content = list;
            side.Children.Add(listScroll);
            Grid.SetColumn(side, 2); Grid.SetRow(side, 0);
            body.Children.Add(side);

            // Footer.
            var bar = new DockPanel { LastChildFill = true, Margin = new Thickness(0, 10, 0, 0) };
            var closeBtn = UiKit.MakeButton("关闭", ButtonVariant.Secondary);
            var selectBtn = UiKit.MakeButton("选择复制", ButtonVariant.Secondary);
            selectBtn.Margin = new Thickness(0, 0, 8, 0);
            selectBtn.IsEnabled = segments.Count > 0;
            DockPanel.SetDock(closeBtn, Dock.Right);
            DockPanel.SetDock(selectBtn, Dock.Right);
            bar.Children.Add(closeBtn);
            bar.Children.Add(selectBtn);
            var status = UiKit.Text("识别 " + segments.Count + " 段 · 百度翻译 · Ctrl+滚轮缩放图片", 11, UiKit.TextSecondary);
            bar.Children.Add(status);
            Grid.SetColumn(bar, 0); Grid.SetColumnSpan(bar, 3); Grid.SetRow(bar, 1);
            body.Children.Add(bar);

            // 按图片尺寸计算窗口大小，不超过工作区。
            double w = 760, h = 520;
            if (pasteImagePng != null)
            {
                using (var ms = new MemoryStream(pasteImagePng))
                {
                    var dec = BitmapDecoder.Create(ms, BitmapCreateOptions.IgnoreColorProfile, BitmapCacheOption.None);
                    if (dec.Frames.Count > 0)
                    {
                        var f = dec.Frames[0];
                        w = Math.Min(f.PixelWidth + SidePanelWidth + 80, SystemParameters.WorkArea.Width * 0.85);
                        h = Math.Min(f.PixelHeight + 140, SystemParameters.WorkArea.Height * 0.85);
                        w = Math.Max(w, 640); h = Math.Max(h, 380);
                    }
                }
            }

            var dlg = OneBoxWindow.Create(owner, "图片翻译", w, h, body, true, IconKey.Translate);

            copyAll.Click += (s, e) =>
            {
                if (CopyText(dst)) status.Text = "已复制全部译文";
            };
            selectBtn.Click += (s, e) => ShowPicker(dlg, segments, status);
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

        static List<string> SplitSegments(string dst)
        {
            if (string.IsNullOrEmpty(dst)) return new List<string>();
            return dst.Split(new[] { "\r\n", "\n" }, StringSplitOptions.None)
                .Select(x => x.Trim())
                .Where(x => x.Length > 0)
                .ToList();
        }

        static bool CopyText(string text)
        {
            if (string.IsNullOrEmpty(text)) return false;
            try { System.Windows.Forms.Clipboard.SetText(text); return true; }
            catch { return false; }
        }

        // One numbered translation segment with its own copy button.
        static Border SegmentCard(int number, string text)
        {
            var row = new DockPanel { LastChildFill = true };
            var num = new Border
            {
                Width = 18, Height = 18, CornerRadius = new CornerRadius(9),
                Background = UiKit.FrozenBrush(ThemeTokens.Active),
                Margin = new Thickness(0, 1, 8, 0), VerticalAlignment = VerticalAlignment.Top,
                Child = new TextBlock { Text = number.ToString(), FontSize = 10, Foreground = UiKit.FrozenBrush(UiKit.TextPrimary),
                    HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center }
            };
            DockPanel.SetDock(num, Dock.Left);
            row.Children.Add(num);
            var copy = new Button
            {
                Content = UiKit.Glyph(UiKit.Glyphs.Copy, 13, UiKit.FrozenBrush(UiKit.TextSecondary)),
                Width = 26, Height = 26, ToolTip = "复制这段", VerticalAlignment = VerticalAlignment.Top
            };
            UiKit.ApplyIconButtonStyle(copy);
            copy.MinWidth = 26; copy.MinHeight = 26;
            System.Windows.Automation.AutomationProperties.SetName(copy, "复制这段");
            DockPanel.SetDock(copy, Dock.Right);
            row.Children.Add(copy);
            var label = new TextBlock
            {
                Text = text, FontSize = 13, LineHeight = 19, TextWrapping = TextWrapping.Wrap,
                Foreground = UiKit.FrozenBrush(UiKit.TextPrimary), VerticalAlignment = VerticalAlignment.Center
            };
            row.Children.Add(label);
            var card = new Border
            {
                Background = UiKit.FrozenBrush(UiKit.CardColor),
                CornerRadius = new CornerRadius(8),
                Padding = new Thickness(10, 8, 6, 8),
                Margin = new Thickness(0, 0, 0, 6),
                Child = row
            };
            copy.Click += (s, e) =>
            {
                if (!CopyText(text)) return;
                copy.Content = UiKit.Text("已复制", 11, UiKit.SuccessColor);
                copy.Width = double.NaN;
                copy.Padding = new Thickness(6, 0, 6, 0);
            };
            return card;
        }

        // 「选择复制译文」：勾选段落后复制，支持全选。
        static void ShowPicker(Window owner, List<string> segments, TextBlock status)
        {
            var root = new DockPanel { Margin = new Thickness(12, 0, 12, 12), LastChildFill = true };
            var boxes = new List<CheckBox>();
            var footer = new DockPanel { LastChildFill = true, Margin = new Thickness(0, 10, 0, 0) };
            var copy = UiKit.MakeButton("复制", ButtonVariant.Primary);
            var all = UiKit.MakeButton("全选", ButtonVariant.Secondary);
            all.Margin = new Thickness(0, 0, 8, 0);
            DockPanel.SetDock(copy, Dock.Right);
            DockPanel.SetDock(all, Dock.Right);
            footer.Children.Add(copy);
            footer.Children.Add(all);
            var count = UiKit.Text("", 11, UiKit.TextSecondary);
            footer.Children.Add(count);
            DockPanel.SetDock(footer, Dock.Bottom);
            root.Children.Add(footer);

            void Refresh()
            {
                int n = boxes.Count(b => b.IsChecked == true);
                count.Text = "已选 " + n + " 段";
                copy.Content = n > 0 ? "复制 " + n + " 段" : "复制";
                copy.IsEnabled = n > 0;
            }

            var list = new StackPanel();
            foreach (var seg in segments)
            {
                var cb = new CheckBox
                {
                    IsChecked = true,
                    Margin = new Thickness(0, 0, 0, 6),
                    Foreground = UiKit.FrozenBrush(UiKit.TextPrimary),
                    Content = new TextBlock { Text = seg, TextWrapping = TextWrapping.Wrap, FontSize = 12, LineHeight = 17 }
                };
                cb.Checked += (_, _) => Refresh();
                cb.Unchecked += (_, _) => Refresh();
                boxes.Add(cb);
                list.Children.Add(new Border
                {
                    Background = UiKit.FrozenBrush(UiKit.CardColor), CornerRadius = new CornerRadius(8),
                    Padding = new Thickness(10, 8, 10, 2), Margin = new Thickness(0, 0, 0, 4), Child = cb
                });
            }
            var scroll = new ScrollViewer { VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Content = list };
            scroll.Resources[typeof(System.Windows.Controls.Primitives.ScrollBar)] = ThemeTokens.CreateDarkScrollBarStyle();
            root.Children.Add(scroll);
            Refresh();

            var sel = OneBoxWindow.Create(owner, "选择复制译文", 360, 420, root, true);
            all.Click += (s, e) =>
            {
                bool allOn = boxes.All(b => b.IsChecked == true);
                foreach (var b in boxes) b.IsChecked = !allOn;
            };
            copy.Click += (s, e) =>
            {
                var picked = boxes.Where(b => b.IsChecked == true)
                    .Select(b => ((TextBlock)b.Content).Text);
                if (CopyText(string.Join(Environment.NewLine, picked)))
                {
                    status.Text = "已复制所选译文";
                    sel.Close();
                }
            };
            sel.ShowDialog();
        }
    }
}
