using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using PowerAudioManager.Commands;

namespace PowerAudioManager
{
    internal static partial class SettingsDialog
    {
        static ScrollViewer BuildScreenshotTab(Window owner, Window dlg, SolidColorBrush fg)
        {
            var stack = new StackPanel { Margin = new Thickness(20) };

            stack.Children.Add(new TextBlock { Text = "截图保存位置", Foreground = Brushes.White, FontWeight = FontWeights.SemiBold, FontSize = 13, Margin = new Thickness(0, 0, 0, 6) });
            var rootRow = new DockPanel { Margin = new Thickness(0, 0, 0, 4), LastChildFill = true };
            var rootBox = new TextBox
            {
                MinHeight = 26, FontSize = 12,
                Background = new SolidColorBrush(Color.FromRgb(42, 39, 60)),
                Foreground = Brushes.White,
                BorderBrush = new SolidColorBrush(Color.FromRgb(80, 75, 120))
            };
            string savedRoot = AppPrefs.GetString("Screenshot.RootDir", "");
            if (string.IsNullOrWhiteSpace(savedRoot))
                savedRoot = System.IO.Path.Combine(System.Environment.GetFolderPath(System.Environment.SpecialFolder.MyPictures), "OneBoxScreenshots");
            rootBox.Text = savedRoot;
            var browseBtn = new Button { Content = "浏览…", Height = 26, FontSize = 12, Margin = new Thickness(8, 0, 0, 0), Padding = new Thickness(10, 0, 10, 0) };
            AppResources.StyleDialogButton(browseBtn, false);
            browseBtn.Click += (s, e) =>
            {
                var fbd = new System.Windows.Forms.FolderBrowserDialog { SelectedPath = rootBox.Text };
                if (fbd.ShowDialog() == System.Windows.Forms.DialogResult.OK)
                    rootBox.Text = fbd.SelectedPath;
            };
            DockPanel.SetDock(browseBtn, Dock.Right);
            rootRow.Children.Add(browseBtn);
            rootRow.Children.Add(rootBox);
            stack.Children.Add(rootRow);

            // 高级：Game Bar 截图默认关闭。开启后启用 HDR 检测 + Game Bar 回退，HDR/全屏游戏截图不走黑。
            stack.Children.Add(new TextBlock { Text = "高级：Game Bar 截图（HDR / 全屏游戏）", Foreground = Brushes.White, FontWeight = FontWeights.SemiBold, FontSize = 13, Margin = new Thickness(0, 0, 0, 6) });
            bool gbEnabled = AppPrefs.GetBool("Screenshot.GameBarEnabled", false);
            var gbToggle = new CheckBox { Content = "启用 Game Bar 截图回退", IsChecked = gbEnabled, Foreground = Brushes.White, FontSize = 12, Margin = new Thickness(0, 0, 0, 8) };
            stack.Children.Add(gbToggle);

            // Game Bar 配置仅在启用时有效，关闭开关时整体变灰。
            var gbPanel = new StackPanel { Margin = new Thickness(0, 0, 0, 0) };
            gbPanel.IsEnabled = gbEnabled;

            gbPanel.Children.Add(new TextBlock { Text = "Game Bar 截图读取位置", Foreground = Brushes.White, FontWeight = FontWeights.SemiBold, FontSize = 13, Margin = new Thickness(0, 0, 0, 6) });
            var gbRow = new DockPanel { Margin = new Thickness(0, 0, 0, 4), LastChildFill = true };
            var gbBox = new TextBox
            {
                MinHeight = 26, FontSize = 12,
                Background = new SolidColorBrush(Color.FromRgb(42, 39, 60)),
                Foreground = Brushes.White,
                BorderBrush = new SolidColorBrush(Color.FromRgb(80, 75, 120))
            };
            string savedGb = AppPrefs.GetString("Screenshot.GameBarDir", "");
            if (string.IsNullOrWhiteSpace(savedGb))
                savedGb = System.IO.Path.Combine(System.Environment.GetFolderPath(System.Environment.SpecialFolder.MyVideos), "Captures");
            gbBox.Text = savedGb;
            var gbBrowseBtn = new Button { Content = "浏览…", Height = 26, FontSize = 12, Margin = new Thickness(8, 0, 0, 0), Padding = new Thickness(10, 0, 10, 0) };
            AppResources.StyleDialogButton(gbBrowseBtn, false);
            gbBrowseBtn.Click += (s, e) =>
            {
                var fbd = new System.Windows.Forms.FolderBrowserDialog { SelectedPath = gbBox.Text };
                if (fbd.ShowDialog() == System.Windows.Forms.DialogResult.OK)
                    gbBox.Text = fbd.SelectedPath;
            };
            DockPanel.SetDock(gbBrowseBtn, Dock.Right);
            gbRow.Children.Add(gbBrowseBtn);
            gbRow.Children.Add(gbBox);
            gbPanel.Children.Add(gbRow);

            gbPanel.Children.Add(new TextBlock { Text = "Game Bar 截图快捷键", Foreground = Brushes.White, FontWeight = FontWeights.SemiBold, FontSize = 13, Margin = new Thickness(0, 0, 0, 6) });
            var gbHk = MakeHotkeyRow(owner, dlg, AppPrefs.GetInt("Screenshot.GameBarHotkey", 0), fg, emptyText: "（未设置，用默认 Win+Alt+PrtScn）", bottomMargin: 8, testOccupancy: false);
            gbPanel.Children.Add(gbHk.Row);
            gbPanel.Children.Add(new TextBlock { Text = "需与 Game Bar 使用相同快捷键，建议选择不含 Win 的组合。", Foreground = fg, FontSize = 10, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 16) });

            stack.Children.Add(gbPanel);
            gbToggle.Checked += (s, e) => gbPanel.IsEnabled = true;
            gbToggle.Unchecked += (s, e) => gbPanel.IsEnabled = false;

            stack.Children.Add(new Border { Height = 1, Background = new SolidColorBrush(Color.FromRgb(80, 75, 120)), Margin = new Thickness(0, 0, 0, 12) });

            stack.Children.Add(new TextBlock { Text = "截图快捷键", Foreground = Brushes.White, FontWeight = FontWeights.SemiBold, FontSize = 13, Margin = new Thickness(0, 0, 0, 6) });
            var hk = MakeHotkeyRow(owner, dlg, AppPrefs.GetInt("Screenshot.Hotkey", 0), fg, bottomMargin: 8);
            stack.Children.Add(hk.Row);

            var btns = MakeButtons();
            ((Button)btns.Children[0]).Click += async (s, e) =>
            {
                if (!TryPersist(dlg,
                    () => AppPrefs.Set(PreferenceKeys.Screenshot.RootDirectory, rootBox.Text.Trim()),
                    () => AppPrefs.Set(PreferenceKeys.Screenshot.GameBarEnabled, gbToggle.IsChecked == true),
                    () => AppPrefs.Set(PreferenceKeys.Screenshot.GameBarDirectory, gbBox.Text.Trim()),
                    () => AppPrefs.Set(PreferenceKeys.Screenshot.GameBarHotkey, gbHk.Value),
                    () => AppPrefs.Set(PreferenceKeys.Hotkeys.Screenshot, hk.Value))) return;
                if (owner is MainWindow mw)
                {
                    var result = await mw.ExecuteCommandAsync(AppCommandId.RuntimeRebuildModules, CommandSource.Settings);
                    if (!result.Success) return;
                }
                dlg.DialogResult = true; dlg.Close();
            };
            ((Button)btns.Children[1]).Click += (s, e) => { dlg.DialogResult = false; dlg.Close(); };
            stack.Children.Add(btns);

            return Scroll(stack);
        }
    }
}

