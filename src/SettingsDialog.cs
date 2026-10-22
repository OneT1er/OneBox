using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Markup;
using System.Windows.Media;
using System.Windows.Shapes;

namespace PowerAudioManager
{
    // 设置对话框：侧栏 + 9 个页面。各页面构建逻辑按文件拆分（partial）。
    // 方向 A（紫影精修）：侧栏圆点选中态、页面标题 + 分组卡片、底部固定操作栏。
    internal static partial class SettingsDialog
    {
        internal const int OneMicTabIndex = 7;
        internal const int AboutTabIndex = 8;
        public static void Show(Window owner)
        {
            Show(owner, 0);
        }

        public static void Show(Window owner, int openTab)
        {
            var fg = UiKit.FrozenBrush(UiKit.TextSecondary);
            var lightText = UiKit.FrozenBrush(UiKit.TextPrimary);
            List<UIElement> tabContents = null;
            ContentControl contentHost = null;
            Action<int> selectTab = null;

            // ---- 侧栏 ----
            var sideBar = new ListBox
            {
                Background = Brushes.Transparent,
                BorderThickness = new Thickness(0),
                Margin = new Thickness(0),
                Padding = new Thickness(0, 4, 0, 0)
            };
            ScrollViewer.SetHorizontalScrollBarVisibility(sideBar, ScrollBarVisibility.Disabled);
            sideBar.Resources[typeof(ScrollBar)] = ThemeTokens.CreateDarkScrollBarStyle();
            sideBar.ItemContainerStyle = SidebarItemStyle();
            var aboutBar = new ListBox
            {
                Background = Brushes.Transparent,
                BorderThickness = new Thickness(0),
                Padding = new Thickness(0, 4, 0, 0)
            };
            aboutBar.ItemContainerStyle = SidebarItemStyle();
            void UpdateSelectionColors(ListBox list)
            {
                foreach (ListBoxItem item in list.Items)
                {
                    if (item.Content is not StackPanel row || row.Children.Count < 2) continue;
                    if (row.Children[0] is Ellipse dot)
                        dot.Fill = item.IsSelected ? UiKit.FrozenBrush(UiKit.AccentColor) : Brushes.Transparent;
                    if (row.Children[1] is TextBlock label)
                    {
                        label.Foreground = UiKit.FrozenBrush(item.IsSelected ? UiKit.TextPrimary : UiKit.TextSecondary);
                        label.FontWeight = item.IsSelected ? FontWeights.SemiBold : FontWeights.Normal;
                    }
                }
            }
            sideBar.SelectionChanged += (s, e) =>
            {
                UpdateSelectionColors(sideBar);
                if (sideBar.SelectedIndex < 0) return;
                aboutBar.SelectedIndex = -1;
                selectTab?.Invoke(sideBar.SelectedIndex);
            };
            aboutBar.SelectionChanged += (s, e) =>
            {
                UpdateSelectionColors(aboutBar);
                if (aboutBar.SelectedIndex < 0) return;
                sideBar.SelectedIndex = -1;
                selectTab?.Invoke(AboutTabIndex);
            };

            contentHost = new ContentControl
            {
                Background = Brushes.Transparent,
                HorizontalContentAlignment = HorizontalAlignment.Stretch,
                VerticalContentAlignment = VerticalAlignment.Stretch
            };

            var layout = new Grid { UseLayoutRounding = true, SnapsToDevicePixels = true, Background = UiKit.FrozenBrush(UiKit.BgColor) };
            layout.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            layout.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            var sideColumn = new DockPanel { Width = 140, Margin = new Thickness(8, 0, 0, 8) };
            DockPanel.SetDock(aboutBar, Dock.Bottom);
            sideColumn.Children.Add(aboutBar);
            sideColumn.Children.Add(sideBar);
            Grid.SetColumn(sideColumn, 0);
            Grid.SetColumn(contentHost, 1);
            layout.Children.Add(sideColumn);
            layout.Children.Add(contentHost);

            var dlg = OneBoxWindow.Create(owner, "OneBox 设置", 560, 700, layout, true);
            dlg.MinWidth = 480;
            dlg.MinHeight = 400;
            // 内容背景不能盖住窗口外壳的底部圆角。
            layout.SizeChanged += (_, _) => layout.Clip = new RectangleGeometry(
                new Rect(0, 0, layout.ActualWidth, layout.ActualHeight), 12, 12);

            tabContents = new List<UIElement>
            {
                BuildGeneralTab(owner, dlg, fg, lightText),
                BuildModulesTab(owner, dlg, fg),
                BuildMemoryTab(owner, dlg, fg),
                BuildTranslateTab(owner, dlg, fg),
                BuildScreenshotTab(owner, dlg, fg),
                BuildClipboardTab(owner, dlg, fg),
                null, // Sensor catalog and its controls exist only while this page is active.
                BuildAudioStudioTab(owner, dlg, fg),
                BuildAboutTab(owner, dlg, fg),
            };
            selectTab = index =>
            {
                contentHost.Content = null;
                if (index != 6) tabContents[6] = null;
                else tabContents[6] ??= BuildTempTab(owner, dlg, fg);
                contentHost.Content = tabContents[index];
            };

            foreach (var name in new[] { "常规", "板块", "内存", "翻译", "截图", "剪贴板", "性能", "OneMic" })
                sideBar.Items.Add(SidebarItem(name));
            aboutBar.Items.Add(SidebarItem("关于"));

            if (openTab == AboutTabIndex)
            {
                aboutBar.SelectedIndex = 0;
            }
            else if (openTab >= 0 && openTab < sideBar.Items.Count)
            {
                sideBar.SelectedIndex = openTab;
            }
            else { sideBar.SelectedIndex = 0; }

            dlg.ShowDialog();
        }

        static ListBoxItem SidebarItem(string text)
        {
            var row = new StackPanel { Orientation = Orientation.Horizontal };
            row.Children.Add(new Ellipse { Width = 6, Height = 6, Fill = Brushes.Transparent, Margin = new Thickness(0, 0, 8, 0), VerticalAlignment = VerticalAlignment.Center });
            row.Children.Add(new TextBlock { Text = text, FontSize = 12, Foreground = UiKit.FrozenBrush(UiKit.TextSecondary), VerticalAlignment = VerticalAlignment.Center });
            var item = new ListBoxItem { Content = row, Height = 36, Padding = new Thickness(10, 0, 10, 0) };
            AutomationProperties.SetName(item, text);
            return item;
        }

        static Style _sidebarItemStyle;
        static Style SidebarItemStyle()
        {
            if (_sidebarItemStyle != null) return _sidebarItemStyle;
            var style = new Style(typeof(ListBoxItem));
            style.Setters.Add(new Setter(ListBoxItem.BackgroundProperty, Brushes.Transparent));
            style.Setters.Add(new Setter(ListBoxItem.BorderBrushProperty, Brushes.Transparent));
            style.Setters.Add(new Setter(ListBoxItem.BorderThicknessProperty, new Thickness(1)));
            style.Setters.Add(new Setter(ListBoxItem.PaddingProperty, new Thickness(0)));
            style.Setters.Add(new Setter(ListBoxItem.VerticalContentAlignmentProperty, VerticalAlignment.Center));
            style.Setters.Add(new Setter(ListBoxItem.CursorProperty, System.Windows.Input.Cursors.Hand));
            style.Setters.Add(new Setter(ListBoxItem.MarginProperty, new Thickness(0, 1, 0, 1)));

            style.Setters.Add(new Setter(Control.HorizontalContentAlignmentProperty, HorizontalAlignment.Stretch));
            style.Setters.Add(new Setter(Control.FocusVisualStyleProperty, null));
            var template = new ControlTemplate(typeof(ListBoxItem));
            var chrome = new FrameworkElementFactory(typeof(Border));
            chrome.SetValue(Border.CornerRadiusProperty, new CornerRadius(8));
            chrome.SetValue(Border.BackgroundProperty, new TemplateBindingExtension(Control.BackgroundProperty));
            chrome.SetValue(Border.BorderBrushProperty, new TemplateBindingExtension(Control.BorderBrushProperty));
            chrome.SetValue(Border.BorderThicknessProperty, new TemplateBindingExtension(Control.BorderThicknessProperty));
            chrome.SetValue(Border.PaddingProperty, new TemplateBindingExtension(Control.PaddingProperty));
            var presenter = new FrameworkElementFactory(typeof(ContentPresenter));
            presenter.SetValue(ContentPresenter.ContentSourceProperty, "Content");
            presenter.SetValue(FrameworkElement.VerticalAlignmentProperty, VerticalAlignment.Center);
            chrome.AppendChild(presenter);
            template.VisualTree = chrome;
            style.Setters.Add(new Setter(Control.TemplateProperty, template));

            // 悬停先于选中，保持当前页面的选中底色。
            var hover = new Trigger { Property = UIElement.IsMouseOverProperty, Value = true };
            hover.Setters.Add(new Setter(Control.BackgroundProperty, UiKit.FrozenBrush(UiKit.CardColor)));
            style.Triggers.Add(hover);

            // 固定边框厚度，切换选中项时文字不横向跳动。
            var sel = new Trigger { Property = ListBoxItem.IsSelectedProperty, Value = true };
            sel.Setters.Add(new Setter(ListBoxItem.BackgroundProperty, UiKit.FrozenBrush(UiKit.RaisedColor)));
            style.Triggers.Add(sel);

            var focus = new Trigger { Property = UIElement.IsKeyboardFocusWithinProperty, Value = true };
            focus.Setters.Add(new Setter(Control.BorderBrushProperty, UiKit.FrozenBrush(ThemeTokens.Accent)));
            style.Triggers.Add(focus);

            style.Seal();
            _sidebarItemStyle = style;
            return style;
        }

        // ---- page scaffolding ------------------------------------------------

        // Page root: 18px title (+ optional trailing status) above the groups.
        static StackPanel NewPage(string title, UIElement trailing = null)
        {
            var stack = new StackPanel { Margin = new Thickness(8, 4, 16, 12) };
            var header = new DockPanel { LastChildFill = true, Margin = new Thickness(0, 0, 0, 14) };
            if (trailing != null)
            {
                if (trailing is FrameworkElement fe) fe.VerticalAlignment = VerticalAlignment.Center;
                DockPanel.SetDock(trailing, Dock.Right);
                header.Children.Add(trailing);
            }
            header.Children.Add(new TextBlock
            {
                Text = title, FontSize = 18, FontWeight = FontWeights.SemiBold,
                Foreground = UiKit.FrozenBrush(UiKit.TextPrimary), VerticalAlignment = VerticalAlignment.Center
            });
            stack.Children.Add(header);
            return stack;
        }

        // Muted wrapped hint text below a group.
        static TextBlock Hint(string text, double top = 0, double bottom = 14)
        {
            return new TextBlock
            {
                Text = text, FontSize = 11, TextWrapping = TextWrapping.Wrap, LineHeight = 16,
                Foreground = UiKit.FrozenBrush(UiKit.TextSecondary),
                Margin = new Thickness(4, top, 4, bottom)
            };
        }

        // Inline sentence: strings become text runs, elements are spaced 6px.
        static StackPanel Sentence(params object[] parts)
        {
            var row = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
            foreach (var part in parts)
            {
                if (part is string s)
                    row.Children.Add(new TextBlock { Text = s, FontSize = 12, Foreground = UiKit.FrozenBrush(UiKit.TextPrimary), VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 6, 0) });
                else if (part is FrameworkElement fe)
                {
                    fe.Margin = new Thickness(0, 0, 6, 0);
                    fe.VerticalAlignment = VerticalAlignment.Center;
                    row.Children.Add(fe);
                }
            }
            return row;
        }

        // A full-width row inside a card hosting a free-form sentence.
        static Border SentenceRow(UIElement content, double minHeight = 44)
        {
            return new Border { MinHeight = minHeight, Padding = new Thickness(12, 6, 12, 6), Child = content };
        }

        // Compact numeric box used inside sentences.
        static TextBox NumBox(string text, double width = 48)
        {
            var box = UiKit.MakeTextBox(text, width);
            box.Padding = new Thickness(4, 4, 4, 4);
            box.TextAlignment = TextAlignment.Center;
            box.HorizontalContentAlignment = HorizontalAlignment.Center;
            box.MinHeight = 28;
            return box;
        }

        // On/off control used as the right side of a SettingsRow.
        static CheckBox Switch(string accessibleName, bool isOn)
        {
            var cb = UiKit.MakeSwitch(null, isOn);
            AutomationProperties.SetName(cb, accessibleName);
            return cb;
        }

        // Dark checkbox for multi-select lists (memory regions, toggles inside sentences).
        static Style _checkStyle;
        static Style DarkCheckStyle()
        {
            if (_checkStyle != null) return _checkStyle;
            const string xaml = @"
<Style xmlns='http://schemas.microsoft.com/winfx/2006/xaml/presentation'
       xmlns:x='http://schemas.microsoft.com/winfx/2006/xaml'
       TargetType='{x:Type CheckBox}'>
  <Setter Property='Foreground' Value='#F2F1FA'/>
  <Setter Property='FontSize' Value='12'/>
  <Setter Property='Cursor' Value='Hand'/>
  <Setter Property='FocusVisualStyle' Value='{x:Null}'/>
  <Setter Property='Template'>
    <Setter.Value>
      <ControlTemplate TargetType='{x:Type CheckBox}'>
        <Grid Background='Transparent'>
          <Grid.ColumnDefinitions>
            <ColumnDefinition Width='Auto'/>
            <ColumnDefinition Width='*'/>
          </Grid.ColumnDefinitions>
          <Border Name='Box' Width='16' Height='16' CornerRadius='4' BorderThickness='1'
                  BorderBrush='#5A5680' Background='Transparent' VerticalAlignment='Top' Margin='0,1,0,0'>
            <Path Name='Mark' Data='M3.5,8 L6.5,11 L12.5,5' Stroke='#17151F' StrokeThickness='2'
                  StrokeStartLineCap='Round' StrokeEndLineCap='Round' StrokeLineJoin='Round' Visibility='Collapsed'/>
          </Border>
          <ContentPresenter Grid.Column='1' Margin='8,0,0,0' VerticalAlignment='Center' RecognizesAccessKey='True'/>
        </Grid>
        <ControlTemplate.Triggers>
          <Trigger Property='IsChecked' Value='True'>
            <Setter TargetName='Box' Property='Background' Value='#8E8CD8'/>
            <Setter TargetName='Box' Property='BorderBrush' Value='#8E8CD8'/>
            <Setter TargetName='Mark' Property='Visibility' Value='Visible'/>
          </Trigger>
          <Trigger Property='IsMouseOver' Value='True'>
            <Setter TargetName='Box' Property='BorderBrush' Value='#C9C7F7'/>
          </Trigger>
          <Trigger Property='IsKeyboardFocused' Value='True'>
            <Setter TargetName='Box' Property='BorderBrush' Value='#C9C7F7'/>
          </Trigger>
          <Trigger Property='IsEnabled' Value='False'>
            <Setter Property='Opacity' Value='0.45'/>
          </Trigger>
        </ControlTemplate.Triggers>
      </ControlTemplate>
    </Setter.Value>
  </Setter>
</Style>";
            _checkStyle = (Style)XamlReader.Parse(xaml);
            _checkStyle.Seal();
            return _checkStyle;
        }

        static CheckBox DarkCheck(object content, bool isChecked)
        {
            return new CheckBox { Content = content, IsChecked = isChecked, Style = DarkCheckStyle(), VerticalAlignment = VerticalAlignment.Center };
        }

        // Collapsible card section: header row with chevron + title + summary.
        static StackPanel CollapsibleGroup(string title, string summary, UIElement body, bool expanded)
        {
            var outer = new StackPanel();
            var chevron = IconCatalog.CreateElement(expanded ? IconKey.ChevronDown : IconKey.ChevronRight, 12, UiKit.FrozenBrush(UiKit.TextSecondary));
            var titleText = UiKit.Text(title, 12, UiKit.TextPrimary);
            var summaryText = UiKit.Text(summary ?? "", 11, UiKit.TextSecondary);
            var head = new DockPanel { LastChildFill = true };
            DockPanel.SetDock(chevron, Dock.Right);
            chevron.Margin = new Thickness(8, 0, 0, 0);
            head.Children.Add(chevron);
            var texts = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
            texts.Children.Add(titleText);
            if (!string.IsNullOrEmpty(summary)) { summaryText.Margin = new Thickness(0, 2, 0, 0); texts.Children.Add(summaryText); }
            head.Children.Add(texts);
            var toggle = new Button { Content = head, HorizontalContentAlignment = HorizontalAlignment.Stretch, MinHeight = 48, Padding = new Thickness(12, 6, 12, 6) };
            UiKit.ApplyFlatStyle(toggle);
            toggle.Background = Brushes.Transparent;
            AutomationProperties.SetName(toggle, title);
            var bodyHost = new Border { Child = body, Visibility = expanded ? Visibility.Visible : Visibility.Collapsed, Padding = new Thickness(0, 0, 0, 6) };
            toggle.Click += (_, _) =>
            {
                bool open = bodyHost.Visibility != Visibility.Visible;
                bodyHost.Visibility = open ? Visibility.Visible : Visibility.Collapsed;
                head.Children.Remove(chevron);
                chevron = IconCatalog.CreateElement(open ? IconKey.ChevronDown : IconKey.ChevronRight, 12, UiKit.FrozenBrush(UiKit.TextSecondary));
                chevron.Margin = new Thickness(8, 0, 0, 0);
                DockPanel.SetDock(chevron, Dock.Right);
                head.Children.Insert(0, chevron);
            };
            outer.Children.Add(toggle);
            outer.Children.Add(bodyHost);
            return outer;
        }

        static StackPanel MakeButtons()
        {
            // Visual order is 取消 · 确定 (primary on the far right).  The panel
            // flows right-to-left so callers keep Children[0] = 确定 and
            // Children[1] = 取消, the contract every page relies on.
            var btns = new StackPanel
            {
                Tag = "SettingsActions", Orientation = Orientation.Horizontal,
                HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Center,
                FlowDirection = FlowDirection.RightToLeft, Margin = new Thickness(0, 18, 0, 0)
            };
            var ok = new Button { Content = "确定", Width = 80, Height = 32, FlowDirection = FlowDirection.LeftToRight };
            var cancel = new Button { Content = "取消", Width = 80, Height = 32, FlowDirection = FlowDirection.LeftToRight, Margin = new Thickness(8, 0, 0, 0) };
            AppResources.StyleDialogButton(ok, true);
            AppResources.StyleDialogButton(cancel, false);
            btns.Children.Add(ok);
            btns.Children.Add(cancel);
            return btns;
        }

        // Registry writes are best-effort for background telemetry/preferences,
        // but a settings dialog must not close while the persisted state is
        // unknown. Callers keep the dialog open and do not continue applying
        // runtime state when any write in the current page fails.
        static bool TryPersist(Window dlg, params Func<bool>[] writes)
        {
            foreach (var write in writes)
            {
                if (write != null && write()) continue;
                OneBoxMessage.Error(dlg, "设置保存失败", "当前运行状态未应用。请检查用户注册表权限后重试。");
                return false;
            }
            return true;
        }

        static CheckBox MakeCb(string label, string key)
        {
            var cb = UiKit.MakeSwitch(label, MainWindow.ModuleVisible(key));
            cb.Margin = new Thickness(0);
            cb.MinHeight = 40;
            cb.Padding = new Thickness(0);
            AutomationProperties.SetName(cb, label);
            return cb;
        }

        // Memory region row: checkbox + name (+ risk tag) + description.
        static CheckBox MakeAreaCb(string label, string tip, string prefKey, bool defChecked, SolidColorBrush fg, bool enabled, bool risky = false)
        {
            var title = new StackPanel { Orientation = Orientation.Horizontal };
            title.Children.Add(new TextBlock { Text = label, FontSize = 12, Foreground = UiKit.FrozenBrush(UiKit.TextPrimary) });
            if (risky) title.Children.Add(UiKit.Tag("可能卡顿", UiKit.WarnColor, UiKit.WarnSurface));
            var content = new StackPanel();
            content.Children.Add(title);
            content.Children.Add(new TextBlock
            {
                Text = tip, FontSize = 11, LineHeight = 15, TextWrapping = TextWrapping.Wrap,
                Foreground = UiKit.FrozenBrush(UiKit.TextSecondary), Margin = new Thickness(0, 2, 0, 0)
            });
            var cb = DarkCheck(content, AppPrefs.GetBool(prefKey, defChecked));
            cb.IsEnabled = enabled;
            cb.Margin = new Thickness(12, 8, 12, 8);
            cb.VerticalAlignment = VerticalAlignment.Top;
            AutomationProperties.SetName(cb, label);
            return cb;
        }

        static TextBox MakeBox()
        {
            return UiKit.MakeTextBox();
        }

        static FrameworkElement Scroll(StackPanel stack)
        {
            var scroll = new ScrollViewer
            {
                Content = stack, VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
                Padding = new Thickness(0), PanningMode = PanningMode.VerticalOnly
            };
            scroll.Resources[typeof(ScrollBar)] = ThemeTokens.CreateDarkScrollBarStyle();
            // 所有设置页由 MakeButtons 创建同一操作栏，移出滚动区并保留原点击处理。
            if (stack.Children.Count == 0 || stack.Children[stack.Children.Count - 1] is not StackPanel actions ||
                !Equals(actions.Tag, "SettingsActions")) return scroll;
            stack.Children.Remove(actions);
            actions.Margin = new Thickness(0);
            var footer = new Border
            {
                Background = UiKit.FrozenBrush(UiKit.BgColor),
                BorderBrush = UiKit.FrozenBrush(UiKit.RaisedColor),
                BorderThickness = new Thickness(0, 1, 0, 0), Padding = new Thickness(16, 0, 16, 0),
                Height = 56,
                Child = actions
            };
            var page = new Grid();
            page.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
            page.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            page.Children.Add(scroll);
            Grid.SetRow(footer, 1);
            page.Children.Add(footer);
            return page;
        }

        static void ConfirmIfDangerous(CheckBox cb, Window dlg, string message)
        {
            cb.Checked += (s, e) =>
            {
                if (!OneBoxMessage.Confirm(dlg, "提示", message, "仍然启用", danger: true)) cb.IsChecked = false;
            };
        }

        // 热键"快捷键芯片 + 修改/设置 + 清除"行。捕获后立即测试注册占用；占用时保留按键值并标注"（被占用）"+ 弹提示。
        // testOccupancy=false 时只捕获不测试（如 Game Bar 热键，注册的是系统组合）。
        static HotkeyRow MakeHotkeyRow(Window owner, Window dlg, int current, SolidColorBrush fg,
            string emptyText = "未设置", int bottomMargin = 4,
            bool testOccupancy = true,
            string occupiedMessage = "该快捷键已被其他程序占用，OneBox 无法注册。\n你可以换一个组合，或先释放占用它的程序。")
        {
            var row = new HotkeyRow { Value = current };
            row.Label = new TextBlock
            {
                FontFamily = UiKit.MonoFont, FontSize = 11,
                VerticalAlignment = VerticalAlignment.Center,
                TextTrimming = TextTrimming.CharacterEllipsis
            };
            var chip = new Border
            {
                CornerRadius = new CornerRadius(6),
                Padding = new Thickness(8, 3, 8, 3),
                VerticalAlignment = VerticalAlignment.Center,
                Child = row.Label
            };
            var setBtn = new Button { Height = 26, FontSize = 12, Padding = new Thickness(8, 0, 8, 0), MinHeight = 26, Margin = new Thickness(6, 0, 0, 0) };
            UiKit.ApplyFlatStyle(setBtn);
            setBtn.Padding = new Thickness(8, 0, 8, 0);
            var clearBtn = UiKit.MakeIconButton(IconKey.Close, "清除快捷键", 26, 12);
            clearBtn.Margin = new Thickness(2, 0, 0, 0);

            void Paint(bool occupied)
            {
                bool set = row.Value != 0;
                row.Label.Text = set ? HotkeyCaptureDialog.Format(row.Value) + (occupied ? "（被占用）" : "") : emptyText;
                row.Label.FontFamily = set ? UiKit.MonoFont : SystemFonts.MessageFontFamily;
                row.Label.Foreground = UiKit.FrozenBrush(occupied ? UiKit.WarnColor : set ? UiKit.TextPrimary : UiKit.TextDisabled);
                chip.Background = set ? UiKit.FrozenBrush(occupied ? UiKit.WarnSurface : UiKit.RaisedColor) : Brushes.Transparent;
                chip.Padding = set ? new Thickness(8, 3, 8, 3) : new Thickness(0);
                setBtn.Content = set ? "修改" : "设置";
                setBtn.Foreground = UiKit.FrozenBrush(set ? UiKit.TextSecondary : UiKit.AccentColor);
                clearBtn.Visibility = set ? Visibility.Visible : Visibility.Collapsed;
            }
            Paint(false);

            setBtn.Click += (s, e) =>
            {
                var captured = HotkeyCaptureDialog.Show(dlg, row.Value);
                if (captured.HasValue)
                {
                    row.Value = captured.Value;
                    if (testOccupancy && owner is MainWindow mw && !mw.TestHotkey(captured.Value))
                    {
                        Paint(true);
                        OneBoxMessage.Error(dlg, "快捷键被占用", occupiedMessage);
                    }
                    else Paint(false);
                }
            };
            clearBtn.Click += (s, e) => { row.Value = 0; Paint(false); };
            row.Row = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 0, bottomMargin), VerticalAlignment = VerticalAlignment.Center };
            row.Row.Children.Add(chip);
            row.Row.Children.Add(setBtn);
            row.Row.Children.Add(clearBtn);
            return row;
        }

        // Hotkey settings row: label on the left, chip + actions on the right.
        static DockPanel HotkeySettingsRow(string label, HotkeyRow hk, string description = null)
        {
            hk.Row.Margin = new Thickness(0);
            return UiKit.SettingsRow(label, hk.Row, description);
        }
    }

    // 热键行的状态容器：Value 供确定按钮读取最新编码（含被占用的），Label/Row 用于布局。
    sealed class HotkeyRow
    {
        public int Value;
        public TextBlock Label;
        public StackPanel Row;
    }
}
