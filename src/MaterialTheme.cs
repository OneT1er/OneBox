using System.Windows;

namespace PowerAudioManager
{
    // Application-wide visual resources.  The name is kept as a compatibility
    // seam for App.Main, while the implementation is now the local OneBox
    // token set and does not depend on a third-party control theme.
    internal static class MaterialTheme
    {
        static bool _applied;
        public static void Apply()
        {
            if (_applied) return;
            _applied = true;
            try { ThemeTokens.Apply(Application.Current); }
            catch (System.Exception ex) { AppLog.Log("Theme.Apply", ex); }
        }
    }
}
