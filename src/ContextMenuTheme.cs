using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Data;
using System.Windows.Documents;
using System.Windows.Media;
using System.Windows.Shapes;

namespace PowerAudioManager
{
    internal static class ContextMenuTheme
    {
        // H.NotifyIcon hosts a WPF ContextMenu in a separate Popup window. The
        // stock MenuItem template brings back the Windows white check gutter and
        // an opaque popup border, even when ContextMenu.Background is dark. Keep
        // the whole menu authored here so the popup chrome, check slot,
        // separators and submenu arrows share the OneBox surface tokens.
        static readonly Color MenuSurface = Color.FromArgb(250,
            ThemeTokens.Background.R, ThemeTokens.Background.G, ThemeTokens.Background.B);
        static readonly Color MenuBorder = ThemeTokens.Border;

        internal static ContextMenu Create(bool includeHeader = false)
        {
            var menu = new ContextMenu
            {
                Background = new SolidColorBrush(MenuSurface),
                Foreground = UiKit.FrozenBrush(UiKit.TextPrimary),
                FontFamily = AppResources.AppFont,
                FontSize = 12,
                BorderBrush = new SolidColorBrush(MenuBorder),
                BorderThickness = new Thickness(1),
                Padding = new Thickness(4),
                MinWidth = includeHeader ? 240 : 200,
                HasDropShadow = false,
                // The outer popup is transparent; only the rounded Border in
                // the template paints the menu surface (no white rectangle).
                Template = CreateContextMenuTemplate(includeHeader)
            };
            menu.Background.Freeze();
            menu.BorderBrush.Freeze();
            // Keep the Popup host from reintroducing the platform shadow even
            // on Windows builds that read the attached service property.
            ContextMenuService.SetHasDropShadow(menu, false);
            menu.Resources[typeof(MenuItem)] = CreateMenuItemStyle();
            menu.Resources[typeof(Separator)] = CreateSeparatorStyle();
            return menu;
        }

        static string SafeVersion()
        {
            try { var v = UpdateChecker.CurrentVersion; return string.IsNullOrEmpty(v) ? "" : "v" + v; }
            catch { return ""; }
        }

        // Menu chrome: rounded surface, a non-interactive header block (brand,
        // version and a status line bound to ContextMenu.Tag), then the items.
        static ControlTemplate CreateContextMenuTemplate(bool includeHeader)
        {
            var template = new ControlTemplate(typeof(ContextMenu));
            var chrome = new FrameworkElementFactory(typeof(Border));
            chrome.Name = "MenuChrome";
            chrome.SetValue(Border.CornerRadiusProperty, new CornerRadius(12));
            chrome.SetValue(Border.SnapsToDevicePixelsProperty, true);
            chrome.SetValue(Border.BackgroundProperty, new TemplateBindingExtension(Control.BackgroundProperty));
            chrome.SetValue(Border.BorderBrushProperty, new TemplateBindingExtension(Control.BorderBrushProperty));
            chrome.SetValue(Border.BorderThicknessProperty, new TemplateBindingExtension(Control.BorderThicknessProperty));
            chrome.SetValue(Border.PaddingProperty, new TemplateBindingExtension(Control.PaddingProperty));

            var stack = new FrameworkElementFactory(typeof(StackPanel));

            if (includeHeader)
            {
                var head = new FrameworkElementFactory(typeof(StackPanel));
                head.SetValue(FrameworkElement.MarginProperty, new Thickness(10, 8, 10, 6));
                var titleRow = new FrameworkElementFactory(typeof(DockPanel));
                titleRow.SetValue(DockPanel.LastChildFillProperty, true);
                var brand = new FrameworkElementFactory(typeof(Path));
                brand.SetValue(Path.DataProperty, IconCatalog.GetGeometry(IconKey.Brand));
                brand.SetValue(FrameworkElement.WidthProperty, 16d);
                brand.SetValue(FrameworkElement.HeightProperty, 16d);
                brand.SetValue(Path.StretchProperty, Stretch.Uniform);
                brand.SetValue(Path.StrokeProperty, UiKit.FrozenBrush(UiKit.AccentColor));
                brand.SetValue(Path.StrokeThicknessProperty, 1.8d);
                brand.SetValue(Path.StrokeLineJoinProperty, PenLineJoin.Round);
                brand.SetValue(FrameworkElement.MarginProperty, new Thickness(0, 0, 8, 0));
                brand.SetValue(DockPanel.DockProperty, Dock.Left);
                titleRow.AppendChild(brand);
                var version = new FrameworkElementFactory(typeof(TextBlock));
                version.SetValue(TextBlock.TextProperty, SafeVersion());
                version.SetValue(TextBlock.FontSizeProperty, 11d);
                version.SetValue(TextBlock.ForegroundProperty, UiKit.FrozenBrush(UiKit.TextSecondary));
                version.SetValue(FrameworkElement.VerticalAlignmentProperty, VerticalAlignment.Center);
                version.SetValue(DockPanel.DockProperty, Dock.Right);
                titleRow.AppendChild(version);
                var title = new FrameworkElementFactory(typeof(TextBlock));
                title.SetValue(TextBlock.TextProperty, "OneBox");
                title.SetValue(TextBlock.FontSizeProperty, 13d);
                title.SetValue(TextBlock.FontWeightProperty, FontWeights.SemiBold);
                title.SetValue(TextBlock.ForegroundProperty, UiKit.FrozenBrush(UiKit.TextPrimary));
                title.SetValue(FrameworkElement.VerticalAlignmentProperty, VerticalAlignment.Center);
                titleRow.AppendChild(title);
                head.AppendChild(titleRow);
                var status = new FrameworkElementFactory(typeof(TextBlock));
                status.Name = "TrayStatus";
                status.SetBinding(TextBlock.TextProperty, new System.Windows.Data.Binding("Tag") { RelativeSource = System.Windows.Data.RelativeSource.TemplatedParent });
                status.SetValue(TextBlock.FontSizeProperty, 11d);
                status.SetValue(TextBlock.ForegroundProperty, UiKit.FrozenBrush(UiKit.TextSecondary));
                status.SetValue(FrameworkElement.MarginProperty, new Thickness(24, 3, 0, 0));
                status.SetValue(TextBlock.TextTrimmingProperty, TextTrimming.CharacterEllipsis);
                head.AppendChild(status);
                stack.AppendChild(head);

                var rule = new FrameworkElementFactory(typeof(Border));
                rule.SetValue(FrameworkElement.HeightProperty, 1d);
                rule.SetValue(Border.BackgroundProperty, UiKit.FrozenBrush(UiKit.RaisedColor));
                rule.SetValue(FrameworkElement.MarginProperty, new Thickness(6, 2, 6, 4));
                stack.AppendChild(rule);
            }

            var items = new FrameworkElementFactory(typeof(ItemsPresenter));
            items.Name = "ItemsPresenter";
            items.SetValue(ItemsPresenter.SnapsToDevicePixelsProperty, true);
            stack.AppendChild(items);
            chrome.AppendChild(stack);
            template.VisualTree = chrome;

            if (includeHeader)
            {
                // Hide the status line when there is nothing to report.
                var emptyStatus = new Trigger { Property = FrameworkElement.TagProperty, Value = null };
                emptyStatus.Setters.Add(new Setter(UIElement.VisibilityProperty, Visibility.Collapsed, "TrayStatus"));
                template.Triggers.Add(emptyStatus);
                var blankStatus = new Trigger { Property = FrameworkElement.TagProperty, Value = "" };
                blankStatus.Setters.Add(new Setter(UIElement.VisibilityProperty, Visibility.Collapsed, "TrayStatus"));
                template.Triggers.Add(blankStatus);
            }
            return template;
        }

        static Style CreateMenuItemStyle()
        {
            var style = new Style(typeof(MenuItem));
            style.Setters.Add(new Setter(Control.ForegroundProperty, UiKit.FrozenBrush(UiKit.TextPrimary)));
            style.Setters.Add(new Setter(Control.BackgroundProperty, Brushes.Transparent));
            style.Setters.Add(new Setter(Control.BorderBrushProperty, Brushes.Transparent));
            style.Setters.Add(new Setter(Control.BorderThicknessProperty, new Thickness(0)));
            style.Setters.Add(new Setter(Control.PaddingProperty, new Thickness(10, 0, 10, 0)));
            style.Setters.Add(new Setter(FrameworkElement.MinHeightProperty, 32d));
            style.Setters.Add(new Setter(FrameworkElement.CursorProperty, System.Windows.Input.Cursors.Hand));
            style.Setters.Add(new Setter(Control.TemplateProperty, CreateMenuItemTemplate()));
            return style;
        }

        // Row: [14px icon slot] header ............ [gesture] [switch | ›]
        // Checkable items render a small switch on the right; the accent
        // "CheckMark" tick sits inside its knob when on.
        static ControlTemplate CreateMenuItemTemplate()
        {
            var template = new ControlTemplate(typeof(MenuItem));
            var root = new FrameworkElementFactory(typeof(Grid));
            var chrome = new FrameworkElementFactory(typeof(Border));
            chrome.Name = "MenuItemChrome";
            chrome.SetValue(Border.CornerRadiusProperty, new CornerRadius(6));
            chrome.SetValue(Border.PaddingProperty, new TemplateBindingExtension(Control.PaddingProperty));
            chrome.SetValue(Border.BackgroundProperty, new TemplateBindingExtension(Control.BackgroundProperty));
            chrome.SetValue(Border.BorderBrushProperty, new TemplateBindingExtension(Control.BorderBrushProperty));
            chrome.SetValue(Border.BorderThicknessProperty, new TemplateBindingExtension(Control.BorderThicknessProperty));
            chrome.SetValue(FrameworkElement.MinHeightProperty, new TemplateBindingExtension(FrameworkElement.MinHeightProperty));

            var row = new FrameworkElementFactory(typeof(DockPanel));
            row.SetValue(DockPanel.LastChildFillProperty, true);
            row.SetValue(FrameworkElement.VerticalAlignmentProperty, VerticalAlignment.Center);

            var iconSlot = new FrameworkElementFactory(typeof(ContentPresenter));
            iconSlot.SetValue(ContentPresenter.ContentSourceProperty, "Icon");
            iconSlot.SetValue(FrameworkElement.WidthProperty, 14d);
            iconSlot.SetValue(FrameworkElement.HeightProperty, 14d);
            iconSlot.SetValue(FrameworkElement.MarginProperty, new Thickness(0, 0, 10, 0));
            iconSlot.SetValue(FrameworkElement.VerticalAlignmentProperty, VerticalAlignment.Center);
            iconSlot.SetValue(DockPanel.DockProperty, Dock.Left);
            row.AppendChild(iconSlot);

            var arrow = new FrameworkElementFactory(typeof(Path));
            arrow.Name = "SubmenuArrow";
            arrow.SetValue(FrameworkElement.WidthProperty, 8d);
            arrow.SetValue(FrameworkElement.HeightProperty, 12d);
            arrow.SetValue(FrameworkElement.MarginProperty, new Thickness(8, 0, 2, 0));
            arrow.SetValue(FrameworkElement.VerticalAlignmentProperty, VerticalAlignment.Center);
            arrow.SetValue(DockPanel.DockProperty, Dock.Right);
            arrow.SetValue(Path.DataProperty, Geometry.Parse("M 1,1 L 6,6 L 1,11"));
            arrow.SetValue(Path.StrokeProperty, UiKit.FrozenBrush(UiKit.TextSecondary));
            arrow.SetValue(Path.StrokeThicknessProperty, 1.8d);
            arrow.SetValue(Path.StrokeStartLineCapProperty, PenLineCap.Round);
            arrow.SetValue(Path.StrokeEndLineCapProperty, PenLineCap.Round);
            arrow.SetValue(UIElement.VisibilityProperty, Visibility.Collapsed);
            row.AppendChild(arrow);

            // 30x16 switch for checkable items.
            var track = new FrameworkElementFactory(typeof(Border));
            track.Name = "SwitchTrack";
            track.SetValue(FrameworkElement.WidthProperty, 30d);
            track.SetValue(FrameworkElement.HeightProperty, 16d);
            track.SetValue(Border.CornerRadiusProperty, new CornerRadius(8));
            track.SetValue(Border.BorderThicknessProperty, new Thickness(1));
            track.SetValue(Border.BorderBrushProperty, UiKit.FrozenBrush(Color.FromRgb(90, 86, 128)));
            track.SetValue(Border.BackgroundProperty, Brushes.Transparent);
            track.SetValue(FrameworkElement.MarginProperty, new Thickness(10, 0, 0, 0));
            track.SetValue(FrameworkElement.VerticalAlignmentProperty, VerticalAlignment.Center);
            track.SetValue(DockPanel.DockProperty, Dock.Right);
            track.SetValue(UIElement.VisibilityProperty, Visibility.Collapsed);
            var knob = new FrameworkElementFactory(typeof(Grid));
            knob.Name = "SwitchKnob";
            knob.SetValue(FrameworkElement.WidthProperty, 10d);
            knob.SetValue(FrameworkElement.HeightProperty, 10d);
            knob.SetValue(FrameworkElement.MarginProperty, new Thickness(2, 0, 2, 0));
            knob.SetValue(FrameworkElement.HorizontalAlignmentProperty, HorizontalAlignment.Left);
            knob.SetValue(FrameworkElement.VerticalAlignmentProperty, VerticalAlignment.Center);
            var knobDot = new FrameworkElementFactory(typeof(Ellipse));
            knobDot.Name = "SwitchDot";
            knobDot.SetValue(Shape.FillProperty, UiKit.FrozenBrush(UiKit.TextSecondary));
            knob.AppendChild(knobDot);
            var check = new FrameworkElementFactory(typeof(Path));
            check.Name = "CheckMark";
            check.SetValue(FrameworkElement.WidthProperty, 6d);
            check.SetValue(FrameworkElement.HeightProperty, 6d);
            check.SetValue(FrameworkElement.HorizontalAlignmentProperty, HorizontalAlignment.Center);
            check.SetValue(FrameworkElement.VerticalAlignmentProperty, VerticalAlignment.Center);
            check.SetValue(Path.StretchProperty, Stretch.Uniform);
            check.SetValue(Path.DataProperty, Geometry.Parse("M 1,6 L 5,10 L 12,1"));
            check.SetValue(Path.StrokeProperty, UiKit.FrozenBrush(UiKit.AccentColor));
            check.SetValue(Path.StrokeThicknessProperty, 1.6d);
            check.SetValue(Path.StrokeStartLineCapProperty, PenLineCap.Round);
            check.SetValue(Path.StrokeEndLineCapProperty, PenLineCap.Round);
            check.SetValue(UIElement.VisibilityProperty, Visibility.Collapsed);
            knob.AppendChild(check);
            track.AppendChild(knob);
            row.AppendChild(track);

            var gesture = new FrameworkElementFactory(typeof(TextBlock));
            gesture.Name = "GestureText";
            gesture.SetValue(TextBlock.TextProperty, new TemplateBindingExtension(MenuItem.InputGestureTextProperty));
            gesture.SetValue(TextBlock.FontSizeProperty, 11d);
            gesture.SetValue(TextBlock.ForegroundProperty, UiKit.FrozenBrush(UiKit.TextTertiary));
            gesture.SetValue(FrameworkElement.MarginProperty, new Thickness(12, 0, 0, 0));
            gesture.SetValue(FrameworkElement.VerticalAlignmentProperty, VerticalAlignment.Center);
            gesture.SetValue(DockPanel.DockProperty, Dock.Right);
            row.AppendChild(gesture);

            var header = new FrameworkElementFactory(typeof(ContentPresenter));
            header.SetValue(ContentPresenter.ContentSourceProperty, "Header");
            header.SetValue(ContentPresenter.RecognizesAccessKeyProperty, true);
            header.SetValue(FrameworkElement.VerticalAlignmentProperty, VerticalAlignment.Center);
            header.SetValue(TextElement.FontFamilyProperty, AppResources.AppFont);
            header.SetValue(TextElement.FontSizeProperty, 12d);
            row.AppendChild(header);

            chrome.AppendChild(row);
            root.AppendChild(chrome);

            // MenuItem opens its children in a second Popup. Include that
            // surface in the authored template too; otherwise a custom row
            // template would make the arrow decorative and hide the submenu.
            var popup = new FrameworkElementFactory(typeof(Popup));
            popup.Name = "PART_Popup";
            popup.SetValue(Popup.PlacementProperty, PlacementMode.Left);
            popup.SetValue(Popup.HorizontalOffsetProperty, 4d);
            popup.SetValue(Popup.AllowsTransparencyProperty, true);
            popup.SetValue(Popup.PopupAnimationProperty, PopupAnimation.Fade);
            popup.SetBinding(Popup.PlacementTargetProperty, new Binding { RelativeSource = RelativeSource.TemplatedParent });
            popup.SetBinding(Popup.IsOpenProperty, new Binding("IsSubmenuOpen")
            {
                RelativeSource = RelativeSource.TemplatedParent,
                Mode = BindingMode.TwoWay
            });
            var submenuChrome = new FrameworkElementFactory(typeof(Border));
            submenuChrome.Name = "SubmenuChrome";
            submenuChrome.SetValue(Border.CornerRadiusProperty, new CornerRadius(10));
            submenuChrome.SetValue(Border.PaddingProperty, new Thickness(4));
            submenuChrome.SetValue(FrameworkElement.MinWidthProperty, 200d);
            submenuChrome.SetValue(Border.BackgroundProperty, UiKit.FrozenBrush(MenuSurface));
            submenuChrome.SetValue(Border.BorderBrushProperty, UiKit.FrozenBrush(MenuBorder));
            submenuChrome.SetValue(Border.BorderThicknessProperty, new Thickness(1));
            var submenuItems = new FrameworkElementFactory(typeof(ItemsPresenter));
            submenuItems.Name = "ItemsPresenter";
            submenuItems.SetValue(ItemsPresenter.SnapsToDevicePixelsProperty, true);
            submenuChrome.AppendChild(submenuItems);
            popup.AppendChild(submenuChrome);
            root.AppendChild(popup);

            template.VisualTree = root;

            var hover = new Trigger { Property = UIElement.IsMouseOverProperty, Value = true };
            hover.Setters.Add(new Setter(Control.BackgroundProperty, UiKit.FrozenBrush(UiKit.RaisedColor), "MenuItemChrome"));
            template.Triggers.Add(hover);

            var highlighted = new Trigger { Property = MenuItem.IsHighlightedProperty, Value = true };
            highlighted.Setters.Add(new Setter(Control.BackgroundProperty, UiKit.FrozenBrush(UiKit.RaisedColor), "MenuItemChrome"));
            template.Triggers.Add(highlighted);

            // MenuItem is not a ButtonBase and has no IsPressedProperty. Use
            // its valid submenu-open state for the pressed/active treatment;
            // ordinary clicks are covered by the highlighted trigger above.
            var active = new Trigger { Property = MenuItem.IsSubmenuOpenProperty, Value = true };
            active.Setters.Add(new Setter(Control.BackgroundProperty, UiKit.FrozenBrush(UiKit.RaisedColor), "MenuItemChrome"));
            template.Triggers.Add(active);

            var checkable = new Trigger { Property = MenuItem.IsCheckableProperty, Value = true };
            checkable.Setters.Add(new Setter(UIElement.VisibilityProperty, Visibility.Visible, "SwitchTrack"));
            template.Triggers.Add(checkable);

            var checkedTrigger = new Trigger { Property = MenuItem.IsCheckedProperty, Value = true };
            checkedTrigger.Setters.Add(new Setter(UIElement.VisibilityProperty, Visibility.Visible, "CheckMark"));
            checkedTrigger.Setters.Add(new Setter(Border.BackgroundProperty, UiKit.FrozenBrush(UiKit.AccentColor), "SwitchTrack"));
            checkedTrigger.Setters.Add(new Setter(Border.BorderBrushProperty, UiKit.FrozenBrush(UiKit.AccentColor), "SwitchTrack"));
            checkedTrigger.Setters.Add(new Setter(FrameworkElement.HorizontalAlignmentProperty, HorizontalAlignment.Right, "SwitchKnob"));
            checkedTrigger.Setters.Add(new Setter(Shape.FillProperty, UiKit.FrozenBrush(ThemeTokens.OnAccent), "SwitchDot"));
            template.Triggers.Add(checkedTrigger);

            var submenu = new Trigger { Property = ItemsControl.HasItemsProperty, Value = true };
            submenu.Setters.Add(new Setter(UIElement.VisibilityProperty, Visibility.Visible, "SubmenuArrow"));
            template.Triggers.Add(submenu);

            var disabled = new Trigger { Property = UIElement.IsEnabledProperty, Value = false };
            disabled.Setters.Add(new Setter(UIElement.OpacityProperty, 0.42));
            template.Triggers.Add(disabled);
            return template;
        }

        static Style CreateSeparatorStyle()
        {
            var style = new Style(typeof(Separator));
            style.Setters.Add(new Setter(Control.BackgroundProperty, UiKit.FrozenBrush(UiKit.RaisedColor)));
            style.Setters.Add(new Setter(Control.ForegroundProperty, Brushes.Transparent));
            style.Setters.Add(new Setter(FrameworkElement.HeightProperty, 1d));
            style.Setters.Add(new Setter(FrameworkElement.MarginProperty, new Thickness(6, 4, 6, 4)));
            style.Setters.Add(new Setter(Control.TemplateProperty, CreateSeparatorTemplate()));
            return style;
        }

        static ControlTemplate CreateSeparatorTemplate()
        {
            var template = new ControlTemplate(typeof(Separator));
            var line = new FrameworkElementFactory(typeof(Border));
            line.SetValue(Border.HeightProperty, 1d);
            line.SetValue(Border.BackgroundProperty, new TemplateBindingExtension(Control.BackgroundProperty));
            line.SetValue(Border.SnapsToDevicePixelsProperty, true);
            template.VisualTree = line;
            return template;
        }

    }
}
