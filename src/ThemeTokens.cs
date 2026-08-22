using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Markup;
using System.Windows.Media;
using System.Windows.Shapes;
using System.Windows.Data;
using BorderElement = System.Windows.Controls.Border;

namespace PowerAudioManager
{
    public static class ThemeTokens
    {
        public const string FlatButtonKey = "OneBox.FlatButton";
        public static readonly Color Accent = Color.FromRgb(142, 140, 216);
        internal static readonly Color Background = Color.FromRgb(28, 26, 40);
        internal static readonly Color TitleSurface = Color.FromRgb(34, 32, 50);
        internal static readonly Color Card = Color.FromRgb(42, 39, 60);
        internal static readonly Color Hover = Color.FromRgb(58, 54, 84);
        internal static readonly Color Active = Color.FromRgb(110, 105, 200);
        internal static readonly Color Border = Color.FromRgb(80, 75, 120);
        internal static readonly Color PrimaryText = Colors.White;
        internal static readonly Color SecondaryText = Color.FromRgb(190, 188, 220);

        internal static SolidColorBrush Brush(Color color)
        {
            var brush = new SolidColorBrush(color);
            brush.Freeze();
            return brush;
        }

        internal static void Apply(Application app)
        {
            if (app == null) return;
            var resources = app.Resources;
            resources["OneBox.AccentBrush"] = Brush(Accent);
            resources["OneBox.BackgroundBrush"] = Brush(Background);
            resources["OneBox.TitleSurfaceBrush"] = Brush(TitleSurface);
            resources["OneBox.CardBrush"] = Brush(Card);
            resources["OneBox.HoverBrush"] = Brush(Hover);
            resources["OneBox.ActiveBrush"] = Brush(Active);
            resources["OneBox.BorderBrush"] = Brush(Border);
            resources["OneBox.PrimaryTextBrush"] = Brush(PrimaryText);
            resources["OneBox.SecondaryTextBrush"] = Brush(SecondaryText);

            var style = new Style(typeof(Button));
            style.Setters.Add(new Setter(Control.BackgroundProperty, Brushes.Transparent));
            style.Setters.Add(new Setter(Control.BorderBrushProperty, Brushes.Transparent));
            style.Setters.Add(new Setter(Control.BorderThicknessProperty, new Thickness(1)));
            style.Setters.Add(new Setter(Control.ForegroundProperty, Brush(SecondaryText)));
            style.Setters.Add(new Setter(Control.PaddingProperty, new Thickness(8, 4, 8, 4)));
            style.Setters.Add(new Setter(FrameworkElement.CursorProperty, Cursors.Hand));
            style.Setters.Add(new Setter(Control.TemplateProperty, CreateButtonTemplate()));
            resources[FlatButtonKey] = style;
        }

        static ControlTemplate CreateButtonTemplate()
        {
            var template = new ControlTemplate(typeof(Button));
            var border = new FrameworkElementFactory(typeof(Border));
            border.Name = "Chrome";
            border.SetValue(System.Windows.Controls.Border.CornerRadiusProperty, new CornerRadius(6));
            border.SetValue(System.Windows.Controls.Border.PaddingProperty, new TemplateBindingExtension(Control.PaddingProperty));
            border.SetValue(System.Windows.Controls.Border.BackgroundProperty, new TemplateBindingExtension(Control.BackgroundProperty));
            border.SetValue(System.Windows.Controls.Border.BorderBrushProperty, new TemplateBindingExtension(Control.BorderBrushProperty));
            border.SetValue(System.Windows.Controls.Border.BorderThicknessProperty, new TemplateBindingExtension(Control.BorderThicknessProperty));
            var content = new FrameworkElementFactory(typeof(ContentPresenter));
            content.SetValue(ContentPresenter.HorizontalAlignmentProperty, HorizontalAlignment.Center);
            content.SetValue(ContentPresenter.VerticalAlignmentProperty, VerticalAlignment.Center);
            content.SetValue(ContentPresenter.ContentSourceProperty, "Content");
            border.AppendChild(content);
            template.VisualTree = border;

            var hover = new Trigger { Property = UIElement.IsMouseOverProperty, Value = true };
            hover.Setters.Add(new Setter(System.Windows.Controls.Border.BackgroundProperty, Brush(Hover), "Chrome"));
            template.Triggers.Add(hover);
            var pressed = new Trigger { Property = ButtonBase.IsPressedProperty, Value = true };
            pressed.Setters.Add(new Setter(System.Windows.Controls.Border.BackgroundProperty, Brush(Active), "Chrome"));
            template.Triggers.Add(pressed);
            var disabled = new Trigger { Property = UIElement.IsEnabledProperty, Value = false };
            disabled.Setters.Add(new Setter(UIElement.OpacityProperty, 0.45));
            template.Triggers.Add(disabled);
            var focused = new Trigger { Property = UIElement.IsKeyboardFocusedProperty, Value = true };
            focused.Setters.Add(new Setter(System.Windows.Controls.Border.BorderBrushProperty, Brush(Accent), "Chrome"));
            focused.Setters.Add(new Setter(System.Windows.Controls.Border.BorderThicknessProperty, new Thickness(1), "Chrome"));
            template.Triggers.Add(focused);
            return template;
        }

        // The stock WPF ComboBox template is intentionally not used here.  It
        // inherits the Windows light theme for the selection box, popup and
        // ComboBoxItem containers, which makes the light surface unreadable
        // against OneBox's purple-shadow palette.  Keep the complete control
        // in code so every dynamically-created settings form gets the same
        // dark surface without relying on an external theme resource.
        internal static Style CreateDarkComboBoxStyle()
        {
            var style = new Style(typeof(ComboBox));
            style.Setters.Add(new Setter(Control.BackgroundProperty, Brush(Card)));
            style.Setters.Add(new Setter(Control.ForegroundProperty, Brush(Color.FromRgb(230, 228, 250))));
            style.Setters.Add(new Setter(Control.BorderBrushProperty, Brush(Border)));
            style.Setters.Add(new Setter(Control.BorderThicknessProperty, new Thickness(1)));
            style.Setters.Add(new Setter(Control.PaddingProperty, new Thickness(8, 0, 8, 0)));
            style.Setters.Add(new Setter(Control.HorizontalContentAlignmentProperty, HorizontalAlignment.Left));
            style.Setters.Add(new Setter(Control.VerticalContentAlignmentProperty, VerticalAlignment.Center));
            style.Setters.Add(new Setter(Control.FocusVisualStyleProperty, null));
            style.Setters.Add(new Setter(Control.TemplateProperty, CreateComboBoxTemplate()));
            style.Setters.Add(new Setter(ItemsControl.ItemContainerStyleProperty, CreateDarkComboBoxItemStyle()));

            var disabled = new Trigger { Property = UIElement.IsEnabledProperty, Value = false };
            disabled.Setters.Add(new Setter(UIElement.OpacityProperty, 0.48));
            style.Triggers.Add(disabled);
            return style;
        }

        internal static Style CreateDarkComboBoxItemStyle()
        {
            var style = new Style(typeof(ComboBoxItem));
            style.Setters.Add(new Setter(Control.BackgroundProperty, Brush(Card)));
            style.Setters.Add(new Setter(Control.ForegroundProperty, Brush(Color.FromRgb(235, 233, 252))));
            style.Setters.Add(new Setter(Control.BorderBrushProperty, Brushes.Transparent));
            style.Setters.Add(new Setter(Control.BorderThicknessProperty, new Thickness(1)));
            style.Setters.Add(new Setter(Control.PaddingProperty, new Thickness(8, 5, 8, 5)));
            style.Setters.Add(new Setter(Control.HorizontalContentAlignmentProperty, HorizontalAlignment.Left));
            style.Setters.Add(new Setter(Control.FocusVisualStyleProperty, null));
            style.Setters.Add(new Setter(Control.TemplateProperty, CreateComboBoxItemTemplate()));

            var hover = new Trigger { Property = UIElement.IsMouseOverProperty, Value = true };
            hover.Setters.Add(new Setter(Control.BackgroundProperty, Brush(Hover)));
            hover.Setters.Add(new Setter(Control.ForegroundProperty, Brushes.White));
            style.Triggers.Add(hover);

            var selected = new Trigger { Property = Selector.IsSelectedProperty, Value = true };
            selected.Setters.Add(new Setter(Control.BackgroundProperty, Brush(Active)));
            selected.Setters.Add(new Setter(Control.ForegroundProperty, Brushes.White));
            selected.Setters.Add(new Setter(Control.BorderBrushProperty, Brush(Accent)));
            style.Triggers.Add(selected);

            var disabled = new Trigger { Property = UIElement.IsEnabledProperty, Value = false };
            disabled.Setters.Add(new Setter(UIElement.OpacityProperty, 0.45));
            style.Triggers.Add(disabled);
            return style;
        }

        static ControlTemplate CreateComboBoxTemplate()
        {
            var template = new ControlTemplate(typeof(ComboBox));
            var chrome = new FrameworkElementFactory(typeof(BorderElement));
            chrome.Name = "Chrome";
            chrome.SetValue(BorderElement.CornerRadiusProperty, new CornerRadius(6));
            chrome.SetValue(BorderElement.SnapsToDevicePixelsProperty, true);
            chrome.SetBinding(BorderElement.BackgroundProperty, new Binding("Background") { RelativeSource = RelativeSource.TemplatedParent });
            chrome.SetBinding(BorderElement.BorderBrushProperty, new Binding("BorderBrush") { RelativeSource = RelativeSource.TemplatedParent });
            chrome.SetBinding(BorderElement.BorderThicknessProperty, new Binding("BorderThickness") { RelativeSource = RelativeSource.TemplatedParent });

            var grid = new FrameworkElementFactory(typeof(Grid));
            var toggle = new FrameworkElementFactory(typeof(ToggleButton));
            toggle.Name = "DropDownToggle";
            toggle.SetValue(ToggleButton.BackgroundProperty, Brushes.Transparent);
            toggle.SetValue(ToggleButton.BorderBrushProperty, Brushes.Transparent);
            toggle.SetValue(ToggleButton.BorderThicknessProperty, new Thickness(0));
            toggle.SetValue(ToggleButton.FocusableProperty, false);
            toggle.SetValue(ToggleButton.ClickModeProperty, ClickMode.Press);
            toggle.SetBinding(ToggleButton.IsCheckedProperty, new Binding("IsDropDownOpen") { RelativeSource = RelativeSource.TemplatedParent, Mode = BindingMode.TwoWay });
            toggle.SetValue(ToggleButton.TemplateProperty, CreateTransparentToggleTemplate());
            grid.AppendChild(toggle);

            var content = new FrameworkElementFactory(typeof(ContentPresenter));
            content.Name = "ContentSite";
            content.SetValue(UIElement.IsHitTestVisibleProperty, false);
            content.SetValue(ContentPresenter.VerticalAlignmentProperty, VerticalAlignment.Center);
            content.SetValue(ContentPresenter.HorizontalAlignmentProperty, HorizontalAlignment.Left);
            content.SetBinding(ContentPresenter.MarginProperty, new Binding("Padding") { RelativeSource = RelativeSource.TemplatedParent });
            content.SetValue(ContentPresenter.ContentProperty, new TemplateBindingExtension(ComboBox.SelectionBoxItemProperty));
            content.SetValue(ContentPresenter.ContentTemplateProperty, new TemplateBindingExtension(ComboBox.SelectionBoxItemTemplateProperty));
            grid.AppendChild(content);

            var arrow = new FrameworkElementFactory(typeof(Path));
            arrow.Name = "Arrow";
            arrow.SetValue(Path.DataProperty, Geometry.Parse("M 0 0 L 5 5 L 10 0"));
            arrow.SetValue(Path.StrokeProperty, Brush(Accent));
            arrow.SetValue(Path.StrokeThicknessProperty, 1.8);
            arrow.SetValue(Path.StrokeLineJoinProperty, PenLineJoin.Round);
            arrow.SetValue(Path.FillProperty, Brushes.Transparent);
            arrow.SetValue(Path.WidthProperty, 12.0);
            arrow.SetValue(Path.HeightProperty, 8.0);
            arrow.SetValue(FrameworkElement.HorizontalAlignmentProperty, HorizontalAlignment.Right);
            arrow.SetValue(FrameworkElement.VerticalAlignmentProperty, VerticalAlignment.Center);
            arrow.SetValue(FrameworkElement.MarginProperty, new Thickness(0, 0, 10, 0));
            arrow.SetValue(UIElement.IsHitTestVisibleProperty, false);
            grid.AppendChild(arrow);

            chrome.AppendChild(grid);
            var root = new FrameworkElementFactory(typeof(Grid));
            root.AppendChild(chrome);

            var open = new Trigger { Property = ComboBox.IsDropDownOpenProperty, Value = true };
            open.Setters.Add(new Setter(BorderElement.BorderBrushProperty, Brush(Accent), "Chrome"));
            open.Setters.Add(new Setter(BorderElement.BackgroundProperty, Brush(Color.FromRgb(48, 44, 70)), "Chrome"));
            template.Triggers.Add(open);
            var hover = new Trigger { Property = UIElement.IsMouseOverProperty, Value = true };
            hover.Setters.Add(new Setter(BorderElement.BorderBrushProperty, Brush(Accent), "Chrome"));
            template.Triggers.Add(hover);
            var focused = new Trigger { Property = UIElement.IsKeyboardFocusWithinProperty, Value = true };
            focused.Setters.Add(new Setter(BorderElement.BorderBrushProperty, Brush(Accent), "Chrome"));
            template.Triggers.Add(focused);

            var popup = new FrameworkElementFactory(typeof(Popup));
            popup.Name = "Popup";
            popup.SetValue(Popup.AllowsTransparencyProperty, true);
            popup.SetValue(Popup.PopupAnimationProperty, PopupAnimation.Fade);
            popup.SetValue(Popup.PlacementProperty, PlacementMode.Bottom);
            popup.SetBinding(Popup.IsOpenProperty, new Binding("IsDropDownOpen") { RelativeSource = RelativeSource.TemplatedParent, Mode = BindingMode.TwoWay });
            popup.SetBinding(Popup.PlacementTargetProperty, new Binding { RelativeSource = RelativeSource.TemplatedParent });
            popup.SetBinding(Popup.WidthProperty, new Binding("ActualWidth") { RelativeSource = RelativeSource.TemplatedParent });

            var popupBorder = new FrameworkElementFactory(typeof(BorderElement));
            popupBorder.Name = "DropDownChrome";
            popupBorder.SetValue(BorderElement.BackgroundProperty, Brush(Card));
            popupBorder.SetValue(BorderElement.BorderBrushProperty, Brush(Accent));
            popupBorder.SetValue(BorderElement.BorderThicknessProperty, new Thickness(1));
            popupBorder.SetValue(BorderElement.CornerRadiusProperty, new CornerRadius(6));
            popupBorder.SetValue(BorderElement.PaddingProperty, new Thickness(3));

            var scroll = new FrameworkElementFactory(typeof(ScrollViewer));
            var scrollStyle = new Style(typeof(ScrollViewer));
            scrollStyle.Resources.Add(typeof(ScrollBar), CreateDarkScrollBarStyle());
            scrollStyle.Setters.Add(new Setter(ScrollViewer.BackgroundProperty, Brush(Card)));
            scrollStyle.Setters.Add(new Setter(ScrollViewer.BorderBrushProperty, Brushes.Transparent));
            scrollStyle.Setters.Add(new Setter(ScrollViewer.HorizontalScrollBarVisibilityProperty, ScrollBarVisibility.Disabled));
            scrollStyle.Setters.Add(new Setter(ScrollViewer.VerticalScrollBarVisibilityProperty, ScrollBarVisibility.Auto));
            scrollStyle.Setters.Add(new Setter(ScrollViewer.MaxHeightProperty, 300.0));
            scrollStyle.Setters.Add(new Setter(ScrollViewer.CanContentScrollProperty, true));
            scroll.SetValue(FrameworkElement.StyleProperty, scrollStyle);
            var items = new FrameworkElementFactory(typeof(ItemsPresenter));
            scroll.AppendChild(items);
            popupBorder.AppendChild(scroll);
            popup.AppendChild(popupBorder);
            root.AppendChild(popup);
            template.VisualTree = root;
            return template;
        }

        // Popup lists can be taller than the sensor panel.  Give their
        // ScrollBar an explicit template too; otherwise WPF may materialize a
        // system-light white track even though the ScrollViewer is dark.
        static Style CreateDarkScrollBarStyle()
        {
            const string xaml = @"
<Style xmlns='http://schemas.microsoft.com/winfx/2006/xaml/presentation'
       xmlns:x='http://schemas.microsoft.com/winfx/2006/xaml'
       TargetType='{x:Type ScrollBar}'>
  <Setter Property='Background' Value='Transparent'/>
  <Setter Property='Foreground' Value='#8E8CD8'/>
  <Setter Property='Width' Value='8'/>
  <Setter Property='Template'>
    <Setter.Value>
      <ControlTemplate TargetType='{x:Type ScrollBar}'>
        <Grid Background='Transparent'>
          <Track Name='PART_Track'
                 Orientation='{TemplateBinding Orientation}'
                 Minimum='{TemplateBinding Minimum}'
                 Maximum='{TemplateBinding Maximum}'
                 Value='{TemplateBinding Value}'
                 ViewportSize='{TemplateBinding ViewportSize}'
                 IsDirectionReversed='true'>
            <Track.Thumb>
              <Thumb Background='#8E8CD8' BorderBrush='#C7C3FF' BorderThickness='1' Opacity='0.9'>
                <Thumb.Template>
                  <ControlTemplate TargetType='{x:Type Thumb}'>
                    <Border Background='{TemplateBinding Background}' BorderBrush='{TemplateBinding BorderBrush}'
                            BorderThickness='{TemplateBinding BorderThickness}' CornerRadius='4'/>
                  </ControlTemplate>
                </Thumb.Template>
              </Thumb>
            </Track.Thumb>
          </Track>
        </Grid>
      </ControlTemplate>
    </Setter.Value>
  </Setter>
  <Style.Triggers>
    <Trigger Property='Orientation' Value='Horizontal'>
      <Setter Property='Height' Value='8'/>
      <Setter Property='Width' Value='Auto'/>
    </Trigger>
  </Style.Triggers>
</Style>";
            return (Style)XamlReader.Parse(xaml);
        }

        static ControlTemplate CreateTransparentToggleTemplate()
        {
            var template = new ControlTemplate(typeof(ToggleButton));
            var border = new FrameworkElementFactory(typeof(BorderElement));
            border.SetValue(BorderElement.BackgroundProperty, Brushes.Transparent);
            border.SetValue(BorderElement.BorderBrushProperty, Brushes.Transparent);
            border.SetValue(BorderElement.BorderThicknessProperty, new Thickness(0));
            template.VisualTree = border;
            return template;
        }

        static ControlTemplate CreateComboBoxItemTemplate()
        {
            var template = new ControlTemplate(typeof(ComboBoxItem));
            var border = new FrameworkElementFactory(typeof(BorderElement));
            border.Name = "ItemChrome";
            border.SetValue(BorderElement.CornerRadiusProperty, new CornerRadius(4));
            border.SetBinding(BorderElement.BackgroundProperty, new Binding("Background") { RelativeSource = RelativeSource.TemplatedParent });
            border.SetBinding(BorderElement.BorderBrushProperty, new Binding("BorderBrush") { RelativeSource = RelativeSource.TemplatedParent });
            border.SetBinding(BorderElement.BorderThicknessProperty, new Binding("BorderThickness") { RelativeSource = RelativeSource.TemplatedParent });
            var content = new FrameworkElementFactory(typeof(ContentPresenter));
            content.SetValue(ContentPresenter.ContentSourceProperty, "Content");
            content.SetBinding(ContentPresenter.MarginProperty, new Binding("Padding") { RelativeSource = RelativeSource.TemplatedParent });
            content.SetBinding(ContentPresenter.HorizontalAlignmentProperty, new Binding("HorizontalContentAlignment") { RelativeSource = RelativeSource.TemplatedParent });
            content.SetBinding(ContentPresenter.VerticalAlignmentProperty, new Binding("VerticalContentAlignment") { RelativeSource = RelativeSource.TemplatedParent });
            border.AppendChild(content);
            template.VisualTree = border;
            return template;
        }
    }
}
