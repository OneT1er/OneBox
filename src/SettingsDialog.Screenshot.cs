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
        static FrameworkElement BuildScreenshotTab(Window owner, Window dlg, SolidColorBrush fg)
        {
            var stack = NewPage("截图");

            // ---- 基本 ----
            var hk = MakeHotkeyRow(owner, dlg, AppPrefs.GetInt("Screenshot.Hotkey", 0), fg);
            var rootBox = MakeBox();
            rootBox.Width = 190;
            rootBox.FontFamily = UiKit.MonoFont;
            rootBox.FontSize = 11;
            System.Windows.Automation.AutomationProperties.SetName(rootBox, "截图保存位置");
            string savedRoot = AppPrefs.GetString("Screenshot.RootDir", "");
            if (string.IsNullOrWhiteSpace(savedRoot))
                savedRoot = System.IO.Path.Combine(System.Environment.GetFolderPath(System.Environment.SpecialFolder.MyPictures), "OneBoxScreenshots");
            rootBox.Text = savedRoot;
            rootBox.ToolTip = savedRoot;
            var browseBtn = UiKit.MakeButton("浏览…", ButtonVariant.Secondary);
            browseBtn.Margin = new Thickness(6, 0, 0, 0);
            browseBtn.Click += (s, e) =>
            {
                var fbd = new System.Windows.Forms.FolderBrowserDialog { SelectedPath = rootBox.Text };
                if (fbd.ShowDialog() == System.Windows.Forms.DialogResult.OK)
                    rootBox.Text = fbd.SelectedPath;
            };
            var rootControl = new StackPanel { Orientation = Orientation.Horizontal };
            rootControl.Children.Add(rootBox);
            rootControl.Children.Add(browseBtn);
            stack.Children.Add(UiKit.SettingsGroup("基本",
                HotkeySettingsRow("截图快捷键", hk),
                UiKit.SettingsRow("保存位置", rootControl)));

            // ---- 截图通知 ----
            var toastOptions = ScreenshotToastOptions.Load();
            int selectedPosition = (int)toastOptions.Position;
            // 2×2 屏幕角落选择器：格子顺序（左上、右上、左下、右下）对应枚举值 3、2、1、0。
            var cornerGrid = new System.Windows.Controls.Primitives.UniformGrid { Rows = 2, Columns = 2 };
            int[] cornerValues = { 3, 2, 1, 0 };
            string[] cornerNames = { "左上角", "右上角", "左下角", "右下角" };
            string cornerGroup = "corner" + Guid.NewGuid().ToString("N");
            for (int i = 0; i < 4; i++)
            {
                int value = cornerValues[i];
                var cell = new RadioButton
                {
                    GroupName = cornerGroup, IsChecked = value == selectedPosition,
                    Style = CornerCellStyle(), ToolTip = cornerNames[i], Margin = new Thickness(1.5)
                };
                System.Windows.Automation.AutomationProperties.SetName(cell, cornerNames[i]);
                cell.Checked += (_, _) => selectedPosition = value;
                cornerGrid.Children.Add(cell);
            }
            var cornerPicker = new Border
            {
                Width = 56, Height = 40, Padding = new Thickness(2),
                Background = UiKit.FrozenBrush(UiKit.BgColor),
                BorderBrush = UiKit.FrozenBrush(UiKit.BorderStrongColor), BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(6), Child = cornerGrid
            };
            System.Windows.Automation.AutomationProperties.SetName(cornerPicker, "弹出位置");

            var durationBox = NumBox(toastOptions.DurationSeconds.ToString());
            System.Windows.Automation.AutomationProperties.SetName(durationBox, "通知显示时间（秒）");
            var compactToggle = Switch("缩略版通知", toastOptions.Compact);
            var excludeToggle = Switch("避免通知被再次截图截入", toastOptions.ExcludeFromCapture);
            ScreenshotToastOptions ReadNotificationOptions()
            {
                if (!int.TryParse(durationBox.Text, out int seconds) || seconds < 1 || seconds > 30)
                {
                    OneBoxMessage.Error(dlg, "OneBox 设置", "通知显示时间请输入 1–30 秒的整数。");
                    durationBox.Focus(); durationBox.SelectAll();
                    return null;
                }
                return new ScreenshotToastOptions { Position = (ScreenshotToastPosition)selectedPosition,
                    DurationSeconds = seconds, Compact = compactToggle.IsChecked == true,
                    ExcludeFromCapture = excludeToggle.IsChecked == true }.Normalize();
            }
            var previewButton = UiKit.MakeButton("预览通知", ButtonVariant.Secondary);
            previewButton.HorizontalAlignment = HorizontalAlignment.Left;
            previewButton.Click += (_, _) => { var options = ReadNotificationOptions(); if (options != null) ScreenshotToast.ShowPreview(options); };
            stack.Children.Add(UiKit.SettingsGroup("截图通知",
                UiKit.SettingsRow("弹出位置", cornerPicker, "显示在截图时前台应用所在屏幕，悬停时暂停关闭"),
                UiKit.SettingsRow("显示时间", Sentence(durationBox, "秒（1–30）")),
                UiKit.SettingsRow("缩略版通知", compactToggle, "小缩略图，更少占屏"),
                UiKit.SettingsRow("避免通知被再次截图截入", excludeToggle, "启用系统截图排除，OneBox 再次截图前会关闭旧通知。部分游戏或第三方截图工具可能不支持排除。"),
                new Border { Padding = new Thickness(12, 4, 12, 12), Child = previewButton }));

            // ---- 游戏截图（可折叠的两个高级区） ----
            // 高级：Game Bar 截图默认关闭。开启后启用 HDR 检测 + Game Bar 回退，HDR/全屏游戏截图不走黑。
            bool gbEnabled = AppPrefs.GetBool("Screenshot.GameBarEnabled", false);
            var gbToggle = Switch("启用 Game Bar 截图回退", gbEnabled);
            // Game Bar 配置仅在启用时有效，关闭开关时整体变灰。
            var gbPanel = new StackPanel { IsEnabled = gbEnabled };
            var gbBox = MakeBox();
            gbBox.FontFamily = UiKit.MonoFont;
            gbBox.FontSize = 11;
            gbBox.Width = 190;
            System.Windows.Automation.AutomationProperties.SetName(gbBox, "Game Bar 截图读取位置");
            string savedGb = AppPrefs.GetString("Screenshot.GameBarDir", "");
            if (string.IsNullOrWhiteSpace(savedGb))
                savedGb = System.IO.Path.Combine(System.Environment.GetFolderPath(System.Environment.SpecialFolder.MyVideos), "Captures");
            gbBox.Text = savedGb;
            var gbBrowseBtn = UiKit.MakeButton("浏览…", ButtonVariant.Secondary);
            gbBrowseBtn.Margin = new Thickness(6, 0, 0, 0);
            gbBrowseBtn.Click += (s, e) =>
            {
                var fbd = new System.Windows.Forms.FolderBrowserDialog { SelectedPath = gbBox.Text };
                if (fbd.ShowDialog() == System.Windows.Forms.DialogResult.OK)
                    gbBox.Text = fbd.SelectedPath;
            };
            var gbPath = new StackPanel { Orientation = Orientation.Horizontal };
            gbPath.Children.Add(gbBox);
            gbPath.Children.Add(gbBrowseBtn);
            var gbHk = MakeHotkeyRow(owner, dlg, AppPrefs.GetInt("Screenshot.GameBarHotkey", 0), fg, emptyText: "默认 Win+Alt+PrtScn", bottomMargin: 0, testOccupancy: false);
            gbPanel.Children.Add(UiKit.SettingsRow("截图读取位置", gbPath));
            gbPanel.Children.Add(HotkeySettingsRow("Game Bar 截图快捷键", gbHk, "需与 Game Bar 使用相同快捷键，建议选择不含 Win 的组合。"));
            gbToggle.Checked += (s, e) => gbPanel.IsEnabled = true;
            gbToggle.Unchecked += (s, e) => gbPanel.IsEnabled = false;
            var gbBody = new StackPanel();
            gbBody.Children.Add(UiKit.SettingsRow("启用 Game Bar 截图回退", gbToggle));
            gbBody.Children.Add(gbPanel);

            // 安全接管：由 Game Bar / Steam / 显卡工具响应实体按键，OneBox 只接管落盘后的图片。
            bool takeoverEnabled = AppPrefs.GetBool("Screenshot.ExternalTakeoverEnabled", false);
            var takeoverToggle = Switch("启用外部截图接管（不模拟按键）", takeoverEnabled);
            var takeoverPanel = new StackPanel { IsEnabled = takeoverEnabled };
            var takeoverBox = MakeBox();
            takeoverBox.Text = ScreenshotService.ExternalTakeoverDir();
            takeoverBox.FontFamily = UiKit.MonoFont;
            takeoverBox.FontSize = 11;
            takeoverBox.Width = 190;
            System.Windows.Automation.AutomationProperties.SetName(takeoverBox, "官方截图保存目录");
            var takeoverBrowseBtn = UiKit.MakeButton("浏览…", ButtonVariant.Secondary);
            takeoverBrowseBtn.Margin = new Thickness(6, 0, 0, 0);
            takeoverBrowseBtn.Click += (_, _) =>
            {
                var fbd = new System.Windows.Forms.FolderBrowserDialog { SelectedPath = takeoverBox.Text };
                if (fbd.ShowDialog() == System.Windows.Forms.DialogResult.OK) takeoverBox.Text = fbd.SelectedPath;
            };
            var takeoverPath = new StackPanel { Orientation = Orientation.Horizontal };
            takeoverPath.Children.Add(takeoverBox);
            takeoverPath.Children.Add(takeoverBrowseBtn);
            takeoverPanel.Children.Add(UiKit.SettingsRow("官方截图保存目录", takeoverPath));
            takeoverToggle.Checked += (_, _) => takeoverPanel.IsEnabled = true;
            takeoverToggle.Unchecked += (_, _) => takeoverPanel.IsEnabled = false;
            var takeoverBody = new StackPanel();
            takeoverBody.Children.Add(UiKit.SettingsRow("启用外部截图接管（不模拟按键）", takeoverToggle,
                "在游戏中按 Game Bar、Steam 或显卡工具的实体截图键；OneBox 会监听新图片、复制归档并弹出提示。支持子目录。"));
            takeoverBody.Children.Add(takeoverPanel);

            stack.Children.Add(UiKit.SettingsGroup("游戏截图",
                CollapsibleGroup("Game Bar 截图回退", "HDR / 全屏游戏 · " + (gbEnabled ? "已开启" : "未开启"), gbBody, false),
                CollapsibleGroup("外部截图接管", "反作弊游戏 · 不模拟按键 · " + (takeoverEnabled ? "已开启" : "未开启"), takeoverBody, false)));

            var btns = MakeButtons();
            ((Button)btns.Children[0]).Click += async (s, e) =>
            {
                var notificationOptions = ReadNotificationOptions();
                if (notificationOptions == null) return;
                if (takeoverToggle.IsChecked == true)
                {
                    string sourceDir = takeoverBox.Text.Trim();
                    if (!System.IO.Directory.Exists(sourceDir))
                    {
                        OneBoxMessage.Error(dlg, "OneBox 设置", "外部截图接管目录不存在，请选择 Game Bar、Steam 或显卡工具实际使用的截图目录。");
                        return;
                    }
                    try
                    {
                        if (string.Equals(System.IO.Path.TrimEndingDirectorySeparator(System.IO.Path.GetFullPath(sourceDir)),
                            System.IO.Path.TrimEndingDirectorySeparator(System.IO.Path.GetFullPath(rootBox.Text.Trim())),
                            StringComparison.OrdinalIgnoreCase))
                        {
                            OneBoxMessage.Error(dlg, "OneBox 设置", "外部截图目录不能与 OneBox 截图保存目录相同，否则无法区分原图和归档副本。");
                            return;
                        }
                    }
                    catch
                    {
                        OneBoxMessage.Error(dlg, "OneBox 设置", "外部截图接管目录格式无效。");
                        return;
                    }
                }
                if (!TryPersist(dlg,
                    () => AppPrefs.Set(PreferenceKeys.Screenshot.ToastPosition, (int)notificationOptions.Position),
                    () => AppPrefs.Set(PreferenceKeys.Screenshot.ToastCompact, notificationOptions.Compact),
                    () => AppPrefs.Set(PreferenceKeys.Screenshot.ToastDurationSeconds, notificationOptions.DurationSeconds),
                    () => AppPrefs.Set(PreferenceKeys.Screenshot.ToastExcludeFromCapture, notificationOptions.ExcludeFromCapture),
                    () => AppPrefs.Set(PreferenceKeys.Screenshot.RootDirectory, rootBox.Text.Trim()),
                    () => AppPrefs.Set(PreferenceKeys.Screenshot.GameBarEnabled, gbToggle.IsChecked == true),
                    () => AppPrefs.Set(PreferenceKeys.Screenshot.GameBarDirectory, gbBox.Text.Trim()),
                    () => AppPrefs.Set(PreferenceKeys.Screenshot.GameBarHotkey, gbHk.Value),
                    () => AppPrefs.Set(PreferenceKeys.Screenshot.ExternalTakeoverEnabled, takeoverToggle.IsChecked == true),
                    () => AppPrefs.Set(PreferenceKeys.Screenshot.ExternalTakeoverDirectory, takeoverBox.Text.Trim()),
                    () => AppPrefs.Set(PreferenceKeys.Hotkeys.Screenshot, hk.Value))) return;
                ScreenshotService.RestartExternalCaptureTakeover();
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

        static Style _cornerCellStyle;
        // One cell of the 2×2 corner picker: a small rounded block, accent when chosen.
        static Style CornerCellStyle()
        {
            if (_cornerCellStyle != null) return _cornerCellStyle;
            const string xaml = @"
<Style xmlns='http://schemas.microsoft.com/winfx/2006/xaml/presentation'
       xmlns:x='http://schemas.microsoft.com/winfx/2006/xaml'
       TargetType='{x:Type RadioButton}'>
  <Setter Property='Cursor' Value='Hand'/>
  <Setter Property='FocusVisualStyle' Value='{x:Null}'/>
  <Setter Property='Template'>
    <Setter.Value>
      <ControlTemplate TargetType='{x:Type RadioButton}'>
        <Border Name='Cell' CornerRadius='3' Background='#2B2839' BorderThickness='1' BorderBrush='Transparent'/>
        <ControlTemplate.Triggers>
          <Trigger Property='IsMouseOver' Value='True'>
            <Setter TargetName='Cell' Property='Background' Value='#3A3650'/>
          </Trigger>
          <Trigger Property='IsChecked' Value='True'>
            <Setter TargetName='Cell' Property='Background' Value='#8E8CD8'/>
          </Trigger>
          <Trigger Property='IsKeyboardFocused' Value='True'>
            <Setter TargetName='Cell' Property='BorderBrush' Value='#C9C7F7'/>
          </Trigger>
        </ControlTemplate.Triggers>
      </ControlTemplate>
    </Setter.Value>
  </Setter>
</Style>";
            _cornerCellStyle = (Style)System.Windows.Markup.XamlReader.Parse(xaml);
            _cornerCellStyle.Seal();
            return _cornerCellStyle;
        }
    }
}

