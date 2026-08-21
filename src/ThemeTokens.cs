using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;

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
    }
}
