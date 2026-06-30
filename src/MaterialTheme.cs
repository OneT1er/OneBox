using System;
using System.Windows;
using System.Windows.Media;
using MaterialDesignThemes.Wpf;

namespace PowerAudioManager
{
    // MaterialDesignInXAML 全局主题引导。在 App.Main 中调用一次，合并 MaterialDesign 默认样式 +
    // 主题字典到 Application.Current.Resources，注入紫影深色主题。此后所有窗口和对话框从应用范围解析
    // MaterialDesign 画笔/样式。PaletteHelper.SetTheme 将展开后的颜色资源写入 Application.Current.Resources。
    internal static class MaterialTheme
    {
        // 品牌调色板，与旧版 AppResources 颜色一致，保持紫影设计语言。
        static readonly Color Primary = Color.FromRgb(142, 140, 216);
        static readonly Color Secondary = Color.FromRgb(126, 122, 210);

        // 幂等守卫：每个进程只运行一次 Apply，防止重复合并字典。
        static bool _applied;

        public static void Apply()
        {
            if (_applied) return;
            _applied = true;
            try
            {
                // 合并 MaterialDesign3 默认样式（隐式控件样式 + 主题字典）。PaletteHelper.SetTheme
                // 只写颜色画笔，控件模板/样式在此。5.x 使用 MaterialDesign3.Defaults.xaml（旧的
                // MaterialDesignTheme.Defaults.xaml 已移除，该 URI 运行时会抛 IOException）。
                Application.Current.Resources.MergedDictionaries.Add(
                    new ResourceDictionary { Source = new Uri("pack://application:,,,/MaterialDesignThemes.Wpf;component/Themes/MaterialDesign3.Defaults.xaml") });

                var helper = new PaletteHelper();
                var theme = Theme.Create(BaseTheme.Dark, Primary, Secondary);
                helper.SetTheme(theme);

                // MaterialDesign 默认字体 Roboto，5.x 主题无字体 API。用应用字体覆盖全局
                // MaterialDesignFont 资源键，保证中文文本使用用户选择的字体。
                try { Application.Current.Resources["MaterialDesignFont"] = AppResources.AppFont; } catch { }
            }
            catch (Exception ex)
            {
                // 主题初始化失败不阻止启动，应用以默认 WPF 样式继续运行。
                AppLog.Log("MaterialTheme.Apply", ex);
            }
        }
    }
}
