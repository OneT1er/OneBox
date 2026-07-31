using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using System.IO;

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
            byte[] png = null;
            try { png = RegionCaptureService.CaptureRegion(); }
            catch (Exception ex) { AppLog.Log("ImageTranslate capture", ex); ImageTranslateWindow.Show(this, null, null, "框选截图失败: " + ex.Message); return; }
            if (png == null) return; // cancelled or empty
            string from = AppPrefs.GetString("Translate.From", "auto");
            string to = AppPrefs.GetString("Translate.To", "zh");
            byte[] pngCaptured = png;
            System.Threading.ThreadPool.QueueUserWorkItem(_ =>
            {
                ImageTranslateService.ImageResult res = null;
                try { res = ImageTranslateService.Translate(pngCaptured, from, to); }
                catch (Exception ex) { res = new ImageTranslateService.ImageResult { Error = ex.Message }; }
                Dispatcher.BeginInvoke(new Action(() =>
                {
                    ImageTranslateWindow.Show(this, res.PasteImage, res.Dst, res.Error);
                }));
            });
        }

        public void TranslateClipboardImage()
        {
            try
            {
                if (!System.Windows.Forms.Clipboard.ContainsImage()) { ImageTranslateWindow.Show(this, null, null, "剪贴板里没有图片"); return; }
                using (var img = System.Windows.Forms.Clipboard.GetImage())
                {
                    if (img == null) { ImageTranslateWindow.Show(this, null, null, "剪贴板里没有图片"); return; }
                    using (var ms = new System.IO.MemoryStream())
                    {
                        img.Save(ms, System.Drawing.Imaging.ImageFormat.Png);
                        byte[] png = ms.ToArray();
                        string from = AppPrefs.GetString("Translate.From", "auto");
                        string to = AppPrefs.GetString("Translate.To", "zh");
                        System.Threading.ThreadPool.QueueUserWorkItem(_ =>
                        {
                            ImageTranslateService.ImageResult res = null;
                            try { res = ImageTranslateService.Translate(png, from, to); }
                            catch (Exception ex) { res = new ImageTranslateService.ImageResult { Error = ex.Message }; }
                            Dispatcher.BeginInvoke(new Action(() =>
                            {
                                ImageTranslateWindow.Show(this, res.PasteImage, res.Dst, res.Error);
                            }));
                        });
                    }
                }
            }
            catch (Exception ex) { AppLog.Log("ImageTranslate clipboard", ex); }
        }
    }
}

