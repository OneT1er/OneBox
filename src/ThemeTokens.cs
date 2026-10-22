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
        // Direction A "紫影精修" palette.  Surfaces step up in lightness from the
        // window ground to raised controls; text tokens all pass 4.5:1 on Card.
        public static readonly Color Accent = Color.FromRgb(142, 140, 216);          // #8E8CD8 (brand)
        internal static readonly Color Background = Color.FromRgb(23, 21, 31);       // #17151F window ground
        internal static readonly Color TitleSurface = Color.FromRgb(23, 21, 31);     // title bar merges into ground
        internal static readonly Color Card = Color.FromRgb(33, 30, 44);             // #211E2C grouped card
        internal static readonly Color Raised = Color.FromRgb(43, 40, 57);           // #2B2839 control inside a card
        internal static readonly Color Hover = Color.FromRgb(50, 46, 68);            // #322E44
        internal static readonly Color Active = Color.FromRgb(54, 50, 90);           // #36325A selected / filled track
        internal static readonly Color Pressed = Color.FromRgb(58, 54, 80);          // #3A3650 selected segment
        internal static readonly Color Border = Color.FromRgb(46, 42, 62);           // #2E2A3E hairline
        internal static readonly Color BorderStrong = Color.FromRgb(58, 54, 80);     // #3A3650 inputs
        internal static readonly Color PrimaryText = Color.FromRgb(242, 241, 250);   // #F2F1FA
        internal static readonly Color SecondaryText = Color.FromRgb(169, 166, 196); // #A9A6C4
        internal static readonly Color TertiaryText = Color.FromRgb(142, 139, 174);  // #8E8BAE captions
        internal static readonly Color DisabledText = Color.FromRgb(111, 107, 140);  // #6F6B8C
        internal static readonly Color OnAccent = Color.FromRgb(23, 21, 31);         // text on accent fill
        internal static readonly Color Warn = Color.FromRgb(232, 178, 90);           // #E8B25A
        internal static readonly Color WarnSurface = Color.FromRgb(58, 47, 36);      // #3A2F24
        internal static readonly Color Danger = Color.FromRgb(229, 154, 166);        // #E59AA6
        internal static readonly Color DangerSurface = Color.FromRgb(58, 36, 48);    // #3A2430
        internal static readonly Color Success = Color.FromRgb(111, 207, 151);       // #6FCF97
        internal static readonly Color SuccessSurface = Color.FromRgb(35, 54, 44);   // #23362C

        // Keys for the shared styles registered by Apply().
        public const string PrimaryButtonKey = "OneBox.PrimaryButton";
        public const string SecondaryButtonKey = "OneBox.SecondaryButton";
        public const string DangerButtonKey = "OneBox.DangerButton";
        public const string SwitchKey = "OneBox.Switch";
        public const string SegmentKey = "OneBox.Segment";
        public const string TileSliderKey = "OneBox.TileSlider";

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
            resources["OneBox.RaisedBrush"] = Brush(Raised);
            resources["OneBox.HoverBrush"] = Brush(Hover);
            resources["OneBox.ActiveBrush"] = Brush(Active);
            resources["OneBox.BorderBrush"] = Brush(Border);
            resources["OneBox.PrimaryTextBrush"] = Brush(PrimaryText);
            resources["OneBox.SecondaryTextBrush"] = Brush(SecondaryText);

            resources[FlatButtonKey] = CreateButtonStyle(Colors.Transparent, Colors.Transparent, SecondaryText, Hover, Active, FontWeights.Normal);
            resources[PrimaryButtonKey] = CreateButtonStyle(Accent, Accent, OnAccent,
                Color.FromRgb(176, 173, 240), Color.FromRgb(132, 129, 214), FontWeights.SemiBold);
            resources[SecondaryButtonKey] = CreateButtonStyle(Colors.Transparent, BorderStrong, PrimaryText, Hover, Active, FontWeights.Normal);
            resources[DangerButtonKey] = CreateButtonStyle(Colors.Transparent, Color.FromRgb(90, 58, 68), Danger, DangerSurface, DangerSurface, FontWeights.Normal);
            resources[SwitchKey] = CreateSwitchStyle();
            resources[SegmentKey] = CreateSegmentStyle();
            resources[TileSliderKey] = CreateTileSliderStyle();
            // An implicit style keeps every slider, including ones created by
            // OneMic and the floating window, on the same visual language.
            resources[typeof(Slider)] = CreateUnifiedSliderStyle();
        }

        // Styles are immutable once sealed, so one instance serves every
        // window.  Rebuilding them per control (and re-parsing XAML) was a
        // measurable allocation source when settings pages were reopened.
        static Style _sliderStyle, _tileSliderStyle, _scrollBarStyle, _switchStyle, _segmentStyle;
        static Style _comboDark, _comboLight;

        internal static Style CreateUnifiedSliderStyle()
        {
            if (_sliderStyle != null) return _sliderStyle;
            const string xaml = @"
<Style xmlns='http://schemas.microsoft.com/winfx/2006/xaml/presentation'
       xmlns:x='http://schemas.microsoft.com/winfx/2006/xaml'
       TargetType='{x:Type Slider}'>
  <Setter Property='Height' Value='28'/>
  <Setter Property='MinWidth' Value='80'/>
  <Setter Property='IsMoveToPointEnabled' Value='True'/>
  <Setter Property='FocusVisualStyle' Value='{x:Null}'/>
  <Setter Property='Template'>
    <Setter.Value>
      <ControlTemplate TargetType='{x:Type Slider}'>
        <Grid Height='28' Background='Transparent' SnapsToDevicePixels='True'>
          <Border Name='Rail' Height='4' Margin='9,0' VerticalAlignment='Center'
                  Background='#3A3650' BorderBrush='Transparent'
                  BorderThickness='1' CornerRadius='2'/>
          <Track Name='PART_Track' Margin='9,0' Orientation='Horizontal'
                 Minimum='{TemplateBinding Minimum}' Maximum='{TemplateBinding Maximum}'
                 Value='{TemplateBinding Value}'
                 IsDirectionReversed='{TemplateBinding IsDirectionReversed}'>
            <Track.DecreaseRepeatButton>
              <RepeatButton Command='Slider.DecreaseLarge' Focusable='False'>
                <RepeatButton.Template>
                  <ControlTemplate TargetType='{x:Type RepeatButton}'>
                    <Grid Background='Transparent'>
                      <Border Height='4' VerticalAlignment='Center'
                              Background='#8E8CD8' CornerRadius='2'/>
                    </Grid>
                  </ControlTemplate>
                </RepeatButton.Template>
              </RepeatButton>
            </Track.DecreaseRepeatButton>
            <Track.Thumb>
              <Thumb Width='14' Height='14' Cursor='Hand'>
                <Thumb.Template>
                  <ControlTemplate TargetType='{x:Type Thumb}'>
                    <Ellipse Name='Knob' Fill='#F2F1FA'/>
                    <ControlTemplate.Triggers>
                      <Trigger Property='IsMouseOver' Value='True'>
                        <Setter TargetName='Knob' Property='Fill' Value='#FFFFFF'/>
                      </Trigger>
                      <Trigger Property='IsDragging' Value='True'>
                        <Setter TargetName='Knob' Property='Fill' Value='#C9C7F7'/>
                      </Trigger>
                    </ControlTemplate.Triggers>
                  </ControlTemplate>
                </Thumb.Template>
              </Thumb>
            </Track.Thumb>
            <Track.IncreaseRepeatButton>
              <RepeatButton Command='Slider.IncreaseLarge' Focusable='False'>
                <RepeatButton.Template>
                  <ControlTemplate TargetType='{x:Type RepeatButton}'>
                    <Grid Background='Transparent'/>
                  </ControlTemplate>
                </RepeatButton.Template>
              </RepeatButton>
            </Track.IncreaseRepeatButton>
          </Track>
        </Grid>
        <ControlTemplate.Triggers>
          <Trigger Property='IsKeyboardFocusWithin' Value='True'>
            <Setter TargetName='Rail' Property='BorderBrush' Value='#C9C7F7'/>
          </Trigger>
          <Trigger Property='IsEnabled' Value='False'>
            <Setter Property='Opacity' Value='0.42'/>
          </Trigger>
        </ControlTemplate.Triggers>
      </ControlTemplate>
    </Setter.Value>
  </Setter>
</Style>";
            _sliderStyle = (Style)XamlReader.Parse(xaml);
            _sliderStyle.Seal();
            return _sliderStyle;
        }

        // "磁贴" slider: a 28px rounded block whose filled part is the value,
        // ending in a 2px accent edge.  Used for volume and OneMic gains.
        internal static Style CreateTileSliderStyle()
        {
            if (_tileSliderStyle != null) return _tileSliderStyle;
            const string xaml = @"
<Style xmlns='http://schemas.microsoft.com/winfx/2006/xaml/presentation'
       xmlns:x='http://schemas.microsoft.com/winfx/2006/xaml'
       TargetType='{x:Type Slider}'>
  <Setter Property='Height' Value='28'/>
  <Setter Property='MinWidth' Value='60'/>
  <Setter Property='IsMoveToPointEnabled' Value='True'/>
  <Setter Property='FocusVisualStyle' Value='{x:Null}'/>
  <Setter Property='Cursor' Value='Hand'/>
  <Setter Property='Template'>
    <Setter.Value>
      <ControlTemplate TargetType='{x:Type Slider}'>
        <Border Name='Rail' Background='#17151F' CornerRadius='8' BorderThickness='1'
                BorderBrush='Transparent' ClipToBounds='True' SnapsToDevicePixels='True'>
          <Track Name='PART_Track' Orientation='Horizontal'
                 Minimum='{TemplateBinding Minimum}' Maximum='{TemplateBinding Maximum}'
                 Value='{TemplateBinding Value}'
                 IsDirectionReversed='{TemplateBinding IsDirectionReversed}'>
            <Track.DecreaseRepeatButton>
              <RepeatButton Command='Slider.DecreaseLarge' Focusable='False'>
                <RepeatButton.Template>
                  <ControlTemplate TargetType='{x:Type RepeatButton}'>
                    <Border Background='#36325A' CornerRadius='7,0,0,7'/>
                  </ControlTemplate>
                </RepeatButton.Template>
              </RepeatButton>
            </Track.DecreaseRepeatButton>
            <Track.Thumb>
              <Thumb Width='2'>
                <Thumb.Template>
                  <ControlTemplate TargetType='{x:Type Thumb}'>
                    <Rectangle Name='Edge' Fill='#8E8CD8'/>
                    <ControlTemplate.Triggers>
                      <Trigger Property='IsDragging' Value='True'>
                        <Setter TargetName='Edge' Property='Fill' Value='#C9C7F7'/>
                      </Trigger>
                    </ControlTemplate.Triggers>
                  </ControlTemplate>
                </Thumb.Template>
              </Thumb>
            </Track.Thumb>
            <Track.IncreaseRepeatButton>
              <RepeatButton Command='Slider.IncreaseLarge' Focusable='False'>
                <RepeatButton.Template>
                  <ControlTemplate TargetType='{x:Type RepeatButton}'>
                    <Grid Background='Transparent'/>
                  </ControlTemplate>
                </RepeatButton.Template>
              </RepeatButton>
            </Track.IncreaseRepeatButton>
          </Track>
        </Border>
        <ControlTemplate.Triggers>
          <Trigger Property='IsMouseOver' Value='True'>
            <Setter TargetName='Rail' Property='Background' Value='#1C1A27'/>
          </Trigger>
          <Trigger Property='IsKeyboardFocusWithin' Value='True'>
            <Setter TargetName='Rail' Property='BorderBrush' Value='#8E8CD8'/>
          </Trigger>
          <Trigger Property='IsEnabled' Value='False'>
            <Setter Property='Opacity' Value='0.42'/>
          </Trigger>
        </ControlTemplate.Triggers>
      </ControlTemplate>
    </Setter.Value>
  </Setter>
</Style>";
            _tileSliderStyle = (Style)XamlReader.Parse(xaml);
            _tileSliderStyle.Seal();
            return _tileSliderStyle;
        }

        // CheckBox rendered as a 36x20 switch.  Content (if any) sits to the
        // left so a row reads "label ...... [switch]".
        internal static Style CreateSwitchStyle()
        {
            if (_switchStyle != null) return _switchStyle;
            const string xaml = @"
<Style xmlns='http://schemas.microsoft.com/winfx/2006/xaml/presentation'
       xmlns:x='http://schemas.microsoft.com/winfx/2006/xaml'
       TargetType='{x:Type CheckBox}'>
  <Setter Property='Foreground' Value='#F2F1FA'/>
  <Setter Property='FontSize' Value='12'/>
  <Setter Property='Cursor' Value='Hand'/>
  <Setter Property='FocusVisualStyle' Value='{x:Null}'/>
  <Setter Property='HorizontalContentAlignment' Value='Left'/>
  <Setter Property='Template'>
    <Setter.Value>
      <ControlTemplate TargetType='{x:Type CheckBox}'>
        <Grid Background='Transparent' MinHeight='28'>
          <Grid.ColumnDefinitions>
            <ColumnDefinition Width='*'/>
            <ColumnDefinition Width='Auto'/>
          </Grid.ColumnDefinitions>
          <ContentPresenter Grid.Column='0' VerticalAlignment='Center' Margin='0,0,12,0'
                            HorizontalAlignment='{TemplateBinding HorizontalContentAlignment}'
                            RecognizesAccessKey='True'/>
          <Border Name='Track' Grid.Column='1' Width='36' Height='20' CornerRadius='10'
                  Background='Transparent' BorderBrush='#5A5680' BorderThickness='1'
                  VerticalAlignment='Center'>
            <Ellipse Name='Knob' Width='12' Height='12' Margin='3,0'
                     HorizontalAlignment='Left' Fill='#A9A6C4'/>
          </Border>
        </Grid>
        <ControlTemplate.Triggers>
          <Trigger Property='IsChecked' Value='True'>
            <Setter TargetName='Track' Property='Background' Value='#8E8CD8'/>
            <Setter TargetName='Track' Property='BorderBrush' Value='#8E8CD8'/>
            <Setter TargetName='Knob' Property='HorizontalAlignment' Value='Right'/>
            <Setter TargetName='Knob' Property='Width' Value='14'/>
            <Setter TargetName='Knob' Property='Height' Value='14'/>
            <Setter TargetName='Knob' Property='Margin' Value='2,0'/>
            <Setter TargetName='Knob' Property='Fill' Value='#17151F'/>
          </Trigger>
          <Trigger Property='IsMouseOver' Value='True'>
            <Setter TargetName='Track' Property='BorderBrush' Value='#C9C7F7'/>
          </Trigger>
          <Trigger Property='IsKeyboardFocused' Value='True'>
            <Setter TargetName='Track' Property='BorderBrush' Value='#C9C7F7'/>
          </Trigger>
          <Trigger Property='IsEnabled' Value='False'>
            <Setter Property='Opacity' Value='0.45'/>
          </Trigger>
        </ControlTemplate.Triggers>
      </ControlTemplate>
    </Setter.Value>
  </Setter>
</Style>";
            _switchStyle = (Style)XamlReader.Parse(xaml);
            _switchStyle.Seal();
            return _switchStyle;
        }

        // RadioButton rendered as one segment of a segmented control.  Put
        // several into a UniformGrid inside a #17151F rounded container
        // (UiKit.MakeSegmented does this).
        internal static Style CreateSegmentStyle()
        {
            if (_segmentStyle != null) return _segmentStyle;
            const string xaml = @"
<Style xmlns='http://schemas.microsoft.com/winfx/2006/xaml/presentation'
       xmlns:x='http://schemas.microsoft.com/winfx/2006/xaml'
       TargetType='{x:Type RadioButton}'>
  <Setter Property='Foreground' Value='#A9A6C4'/>
  <Setter Property='FontSize' Value='12'/>
  <Setter Property='Height' Value='28'/>
  <Setter Property='Cursor' Value='Hand'/>
  <Setter Property='FocusVisualStyle' Value='{x:Null}'/>
  <Setter Property='Template'>
    <Setter.Value>
      <ControlTemplate TargetType='{x:Type RadioButton}'>
        <Border Name='Chrome' CornerRadius='6' Background='Transparent' Padding='8,0'
                BorderThickness='1' BorderBrush='Transparent'>
          <ContentPresenter HorizontalAlignment='Center' VerticalAlignment='Center'/>
        </Border>
        <ControlTemplate.Triggers>
          <Trigger Property='IsMouseOver' Value='True'>
            <Setter TargetName='Chrome' Property='Background' Value='#2B2839'/>
          </Trigger>
          <Trigger Property='IsChecked' Value='True'>
            <Setter TargetName='Chrome' Property='Background' Value='#3A3650'/>
            <Setter Property='Foreground' Value='#F2F1FA'/>
            <Setter Property='FontWeight' Value='SemiBold'/>
          </Trigger>
          <Trigger Property='IsKeyboardFocused' Value='True'>
            <Setter TargetName='Chrome' Property='BorderBrush' Value='#8E8CD8'/>
          </Trigger>
          <Trigger Property='IsEnabled' Value='False'>
            <Setter Property='Opacity' Value='0.45'/>
          </Trigger>
        </ControlTemplate.Triggers>
      </ControlTemplate>
    </Setter.Value>
  </Setter>
</Style>";
            _segmentStyle = (Style)XamlReader.Parse(xaml);
            _segmentStyle.Seal();
            return _segmentStyle;
        }

        static Style CreateButtonStyle(Color background, Color border, Color foreground,
            Color hover, Color pressed, FontWeight weight)
        {
            var style = new Style(typeof(Button));
            style.Setters.Add(new Setter(Control.BackgroundProperty, Brush(background)));
            style.Setters.Add(new Setter(Control.BorderBrushProperty, Brush(border)));
            style.Setters.Add(new Setter(Control.BorderThicknessProperty, new Thickness(1)));
            style.Setters.Add(new Setter(Control.ForegroundProperty, Brush(foreground)));
            style.Setters.Add(new Setter(Control.FontWeightProperty, weight));
            style.Setters.Add(new Setter(Control.FontSizeProperty, 12.0));
            style.Setters.Add(new Setter(Control.PaddingProperty, new Thickness(12, 0, 12, 0)));
            style.Setters.Add(new Setter(FrameworkElement.MinHeightProperty, 30.0));
            style.Setters.Add(new Setter(FrameworkElement.CursorProperty, Cursors.Hand));
            style.Setters.Add(new Setter(FrameworkElement.FocusVisualStyleProperty, null));
            style.Setters.Add(new Setter(Control.TemplateProperty, CreateButtonTemplate(hover, pressed)));
            style.Seal();
            return style;
        }

        static ControlTemplate CreateButtonTemplate(Color hoverColor, Color pressedColor)
        {
            var template = new ControlTemplate(typeof(Button));
            var border = new FrameworkElementFactory(typeof(Border));
            border.Name = "Chrome";
            border.SetValue(System.Windows.Controls.Border.CornerRadiusProperty, new CornerRadius(6));
            border.SetValue(System.Windows.Controls.Border.PaddingProperty, new TemplateBindingExtension(Control.PaddingProperty));
            border.SetValue(System.Windows.Controls.Border.BackgroundProperty, new TemplateBindingExtension(Control.BackgroundProperty));
            border.SetValue(System.Windows.Controls.Border.BorderBrushProperty, new TemplateBindingExtension(Control.BorderBrushProperty));
            border.SetValue(System.Windows.Controls.Border.BorderThicknessProperty, new TemplateBindingExtension(Control.BorderThicknessProperty));
            border.SetValue(UIElement.SnapsToDevicePixelsProperty, true);
            var content = new FrameworkElementFactory(typeof(ContentPresenter));
            content.SetValue(ContentPresenter.HorizontalAlignmentProperty, new TemplateBindingExtension(Control.HorizontalContentAlignmentProperty));
            content.SetValue(ContentPresenter.VerticalAlignmentProperty, VerticalAlignment.Center);
            content.SetValue(ContentPresenter.ContentSourceProperty, "Content");
            border.AppendChild(content);
            template.VisualTree = border;

            var hover = new Trigger { Property = UIElement.IsMouseOverProperty, Value = true };
            hover.Setters.Add(new Setter(System.Windows.Controls.Border.BackgroundProperty, Brush(hoverColor), "Chrome"));
            template.Triggers.Add(hover);
            var pressed = new Trigger { Property = ButtonBase.IsPressedProperty, Value = true };
            pressed.Setters.Add(new Setter(System.Windows.Controls.Border.BackgroundProperty, Brush(pressedColor), "Chrome"));
            template.Triggers.Add(pressed);
            var disabled = new Trigger { Property = UIElement.IsEnabledProperty, Value = false };
            disabled.Setters.Add(new Setter(UIElement.OpacityProperty, 0.45));
            template.Triggers.Add(disabled);
            var focused = new Trigger { Property = UIElement.IsKeyboardFocusedProperty, Value = true };
            focused.Setters.Add(new Setter(System.Windows.Controls.Border.BorderBrushProperty, Brush(Accent), "Chrome"));
            template.Triggers.Add(focused);
            return template;
        }
        // The stock WPF ComboBox template is intentionally not used here.  It
        // inherits the Windows light theme for the selection box, popup and
        // ComboBoxItem containers, which makes the light surface unreadable
        // against OneBox's purple-shadow palette.  Keep the complete control
        // in code so every dynamically-created settings form gets the same
        // dark surface without relying on an external theme resource.
        internal static Style CreateDarkComboBoxStyle(bool light = false)
        {
            var cached = light ? _comboLight : _comboDark;
            if (cached != null) return cached;
            var style = new Style(typeof(ComboBox));
            style.Setters.Add(new Setter(Control.BackgroundProperty, light ? Brushes.White : Brush(Background)));
            style.Setters.Add(new Setter(Control.ForegroundProperty, Brush(light ? Color.FromRgb(43, 39, 60) : PrimaryText)));
            style.Setters.Add(new Setter(Control.BorderBrushProperty, Brush(BorderStrong)));
            style.Setters.Add(new Setter(FrameworkElement.MinHeightProperty, 30.0));
            style.Setters.Add(new Setter(Control.BorderThicknessProperty, new Thickness(1)));
            style.Setters.Add(new Setter(Control.PaddingProperty, new Thickness(8, 0, 8, 0)));
            style.Setters.Add(new Setter(Control.HorizontalContentAlignmentProperty, HorizontalAlignment.Left));
            style.Setters.Add(new Setter(Control.VerticalContentAlignmentProperty, VerticalAlignment.Center));
            style.Setters.Add(new Setter(Control.FocusVisualStyleProperty, null));
            style.Setters.Add(new Setter(Control.TemplateProperty, CreateComboBoxTemplate(light)));
            style.Setters.Add(new Setter(ItemsControl.ItemContainerStyleProperty, CreateDarkComboBoxItemStyle(light)));

            var disabled = new Trigger { Property = UIElement.IsEnabledProperty, Value = false };
            disabled.Setters.Add(new Setter(UIElement.OpacityProperty, 0.48));
            style.Triggers.Add(disabled);
            style.Seal();
            if (light) _comboLight = style; else _comboDark = style;
            return style;
        }

        internal static Style CreateDarkComboBoxItemStyle(bool light = false)
        {
            var style = new Style(typeof(ComboBoxItem));
            style.Setters.Add(new Setter(Control.BackgroundProperty, light ? Brushes.White : Brush(Card)));
            style.Setters.Add(new Setter(Control.ForegroundProperty, Brush(light ? Color.FromRgb(43, 39, 60) : PrimaryText)));
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
            selected.Setters.Add(new Setter(Control.ForegroundProperty, Brush(PrimaryText)));
            selected.Setters.Add(new Setter(Control.BorderBrushProperty, Brushes.Transparent));
            style.Triggers.Add(selected);

            var disabled = new Trigger { Property = UIElement.IsEnabledProperty, Value = false };
            disabled.Setters.Add(new Setter(UIElement.OpacityProperty, 0.45));
            style.Triggers.Add(disabled);
            return style;
        }

        static ControlTemplate CreateComboBoxTemplate(bool light = false)
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
            open.Setters.Add(new Setter(BorderElement.BackgroundProperty, Brush(Card), "Chrome"));
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
            popupBorder.SetValue(BorderElement.BackgroundProperty, light ? Brushes.White : Brush(Card));
            popupBorder.SetValue(BorderElement.BorderBrushProperty, Brush(BorderStrong));
            popupBorder.SetValue(BorderElement.BorderThicknessProperty, new Thickness(1));
            popupBorder.SetValue(BorderElement.CornerRadiusProperty, new CornerRadius(8));
            popupBorder.SetValue(BorderElement.PaddingProperty, new Thickness(3));

            var scroll = new FrameworkElementFactory(typeof(ScrollViewer));
            var scrollStyle = new Style(typeof(ScrollViewer));
            scrollStyle.Resources.Add(typeof(ScrollBar), CreateDarkScrollBarStyle());
            scrollStyle.Setters.Add(new Setter(ScrollViewer.BackgroundProperty, light ? Brushes.White : Brush(Card)));
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
        internal static Style CreateDarkScrollBarStyle()
        {
            if (_scrollBarStyle != null) return _scrollBarStyle;
            const string xaml = @"
<Style xmlns='http://schemas.microsoft.com/winfx/2006/xaml/presentation'
       xmlns:x='http://schemas.microsoft.com/winfx/2006/xaml'
       TargetType='{x:Type ScrollBar}'>
  <Setter Property='Background' Value='Transparent'/>
  <Setter Property='Foreground' Value='#5A5680'/>
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
              <Thumb Background='#4A4660' BorderBrush='Transparent' BorderThickness='0' Opacity='1'>
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
            _scrollBarStyle = (Style)XamlReader.Parse(xaml);
            _scrollBarStyle.Seal();
            return _scrollBarStyle;
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
