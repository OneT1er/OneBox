using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using PowerAudioManager.Commands;

namespace PowerAudioManager
{
    // 翻译：文本翻译窗口（复用单实例）、剪贴板翻译、图片翻译（框选 / 剪贴板 → 百度图片 API）。
    public partial class MainWindow : Window
    {
        void OpenTranslateWindow(string initialText)
        {
            if (_translateWindow == null || !_translateWindow.IsLoaded)
            {
                _translateWindow = new TranslateWindow { FontFamily = this.FontFamily };
                _translateWindow.Closed += (s, e) => _translateWindow = null;
            }
            _translateWindow.Show();
            _translateWindow.Activate();
            if (!string.IsNullOrEmpty(initialText)) _translateWindow.RunTranslation(initialText);
        }

        void TranslateFromClipboard()
        {
            try
            {
                if (Clipboard.ContainsText())
                {
                    string txt = Clipboard.GetText();
                    if (!string.IsNullOrEmpty(txt))
                    {
                        OpenTranslateWindow(txt);
                    }
                }
            }
            catch { }
        }

        void HandleImageTranslateHotkey()
        {
            _ = ExecuteCommandAsync(AppCommandId.TranslateImageRegion, CommandSource.Hotkey);
        }

        public void TranslateClipboardImage()
        {
            _ = ExecuteCommandAsync(AppCommandId.TranslateImageClipboard, CommandSource.MainWindow);
        }

        internal async Task<CommandResult> TranslateImageRegionAsync(CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            byte[] png = RegionCaptureService.CaptureRegion();
            if (png == null) return CommandResult.Cancelled();
            cancellationToken.ThrowIfCancellationRequested();
            return await TranslateImageBytesAsync(png, cancellationToken);
        }

        internal async Task<CommandResult> TranslateClipboardImageAsync(CancellationToken cancellationToken)
        {
            try
            {
                if (!System.Windows.Forms.Clipboard.ContainsImage())
                    return CommandResult.Fail(CommandErrorCode.NotAvailable, "剪贴板里没有图片。");
                using (var img = System.Windows.Forms.Clipboard.GetImage())
                {
                    if (img == null) return CommandResult.Fail(CommandErrorCode.NotAvailable, "剪贴板里没有图片。");
                    using (var ms = new System.IO.MemoryStream())
                    {
                        img.Save(ms, System.Drawing.Imaging.ImageFormat.Png);
                        return await TranslateImageBytesAsync(ms.ToArray(), cancellationToken);
                    }
                }
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex)
            {
                AppLog.Log("ImageTranslate clipboard", ex);
                return CommandResult.Fail(CommandErrorCode.Failed, "图片翻译失败：" + ex.Message);
            }
        }

        async Task<CommandResult> TranslateImageBytesAsync(byte[] png, CancellationToken cancellationToken)
        {
            string from = AppPrefs.Get(PreferenceKeys.Translate.From);
            string to = AppPrefs.Get(PreferenceKeys.Translate.To);
            var result = await ImageTranslateService.TranslateAsync(png, from, to, cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
            ImageTranslateWindow.Show(this, result.PasteImage, result.Dst, result.Error);
            return string.IsNullOrEmpty(result.Error)
                ? CommandResult.Ok(result)
                : CommandResult.Fail(CommandErrorCode.Failed, result.Error, result);
        }
    }
}

