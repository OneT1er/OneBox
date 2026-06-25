using System;
using System.Windows;
using System.Windows.Media;
using MaterialDesignThemes.Wpf;

namespace PowerAudioManager
{
    // Global MaterialDesignInXAML bootstrap. Called once from App.Main before the
    // window is built: merges the MaterialDesign defaults (implicit control styles
    // + theme dictionaries) into Application.Current.Resources and stamps a dark,
    // 紫影-branded theme. After this every window — the floating card and every
    // dialog — resolves MaterialDesign brushes/styles from the application scope.
    //
    // Why global now: the earlier pilot kept the theme per-window to shield the
    // floating window. The user opted to refactor the floating window too, so a
    // single application-scope theme gives one consistent design language across
    // the whole app with the least code. PaletteHelper.SetTheme writes the theme
    // (the expanded MaterialDesign.Brush.* colour resources) to
    // Application.Current.Resources, which is exactly the scope MaterialDesign's
    // implicit styles resolve against.
    internal static class MaterialTheme
    {
        // Brand palette — identical to the old hand-rolled AppResources colours so
        // the app reads as the same 紫影 design language, just with MaterialDesign's
        // control chrome, elevation and motion layered on.
        static readonly Color Primary = Color.FromRgb(142, 140, 216);   // #8E8CD8 紫影
        static readonly Color Secondary = Color.FromRgb(126, 122, 210); // #7E7AD2

        // Idempotent guard: Apply runs once per process. Guarded because App.Main
        // is the only caller, but a second call would double-merge dictionaries.
        static bool _applied;

        public static void Apply()
        {
            if (_applied) return;
            _applied = true;
            try
            {
                // Merge the full MaterialDesign3 defaults (implicit control styles +
                // the theme dictionaries that back the MaterialDesign.* style keys)
                // into the application resources. PaletteHelper.SetTheme only writes
                // the colour brushes; the control templates/styles live here.
                // 5.x ships MaterialDesign3.Defaults.xaml (the older
                // MaterialDesignTheme.Defaults.xaml was removed — that URI throws
                // IOException at runtime, which is why this must be MD3).
                Application.Current.Resources.MergedDictionaries.Add(
                    new ResourceDictionary { Source = new Uri("pack://application:,,,/MaterialDesignThemes.Wpf;component/Themes/MaterialDesign3.Defaults.xaml") });

                var helper = new PaletteHelper();
                var theme = Theme.Create(BaseTheme.Dark, Primary, Secondary);
                helper.SetTheme(theme);

                // MaterialDesign defaults to Roboto. Override the global font key
                // (MaterialDesignFont) with the user-selected app font so Chinese
                // text keeps its chosen family. Theme has no font API in 5.x; the
                // font is a plain application-scope resource the styles bind to.
                try { Application.Current.Resources["MaterialDesignFont"] = AppResources.AppFont; } catch { }
            }
            catch (Exception ex)
            {
                // Theming failure must never stop startup — the app still runs with
                // default WPF chrome if MaterialDesign couldn't initialise.
                AppLog.Log("MaterialTheme.Apply", ex);
            }
        }
    }
}
