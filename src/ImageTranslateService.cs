using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Text;
using System.Text.Json;
using System.Windows;

namespace PowerAudioManager
{
    // Baidu image-translation API (https://fanyi-api.baidu.com/ait/api/picture/translate).
    // OCRs text in an image and returns a paste=1 "整图贴合" image (original text erased,
    // translation overlaid) plus the full translated text. Reuses the same AppId/Key as
    // the text translator (TranslateService) — the key is the Bearer token, verified to
    // work for this endpoint too (no separate OAuth).
    public static class ImageTranslateService
    {
        const string Endpoint = "https://fanyi-api.baidu.com/ait/api/picture/translate";

        public class ImageResult
        {
            public byte[] PasteImage;      // the tonemapped/overlaid PNG (paste=1), null on failure
            public string Dst;              // full translated text
            public string Src;              // full source OCR text
            public string Error;            // non-null on failure
        }

        // Translate an in-memory image. `imageBytes` should be PNG/JPG (Baidu accepts both;
        // we send as-is). from/to reuse the text translator's language settings.
        public static ImageResult Translate(byte[] imageBytes, string from, string to)
        {
            var r = new ImageResult();
            if (imageBytes == null || imageBytes.Length == 0) { r.Error = "无图片"; return r; }
            if (imageBytes.Length > 5 * 1024 * 1024) { r.Error = "图片超过 5MB 上限"; return r; }
            try
            {
                string appId = TranslateService.GetAppId();
                string key = TranslateService.GetKey();
                if (string.IsNullOrEmpty(appId) || string.IsNullOrEmpty(key))
                {
                    r.Error = "未配置百度翻译 AppId/Key，请先在设置→翻译里填写。";
                    return r;
                }

                ServicePointManager.SecurityProtocol = SecurityProtocolType.Tls12 | ServicePointManager.SecurityProtocol;
                var req = (HttpWebRequest)WebRequest.Create(Endpoint);
                req.Method = "POST";
                req.ContentType = "application/json";
                req.Headers["Authorization"] = "Bearer " + key;
                req.Timeout = 30000; // image OCR can be slower than text
                req.ReadWriteTimeout = 30000;

                string fromArg = string.IsNullOrEmpty(from) ? "auto" : from;
                string toArg = string.IsNullOrEmpty(to) ? "zh" : to;
                var payload = new Dictionary<string, object>
                {
                    ["from"] = fromArg,
                    ["to"] = toArg,
                    ["appid"] = appId,
                    ["content"] = Convert.ToBase64String(imageBytes),
                    ["paste"] = 1,          // 整图贴合: erase source, overlay translation
                    ["need_intervene"] = 0,
                    ["view_type"] = 0,       // 通用擦除
                    ["model_type"] = "nmt"
                };
                var body = JsonSerializer.SerializeToUtf8Bytes(payload);
                req.ContentLength = body.Length;
                using (var s = req.GetRequestStream()) s.Write(body, 0, body.Length);

                string json;
                using (var resp = (HttpWebResponse)req.GetResponse())
                using (var rs = resp.GetResponseStream())
                using (var rd = new StreamReader(rs, Encoding.UTF8))
                    json = rd.ReadToEnd();

                using (var doc = JsonDocument.Parse(json))
                {
                    var root = doc.RootElement;
                    // Baidu error responses carry error_code/error_msg.
                    if (root.ValueKind == JsonValueKind.Object && root.TryGetProperty("error_code", out var ec) &&
                        ec.ValueKind == JsonValueKind.String)
                    {
                        string code = ec.GetString();
                        if (!string.IsNullOrEmpty(code) && code != "0" && code != "52000")
                        {
                            string msg = root.TryGetProperty("error_msg", out var em) && em.ValueKind == JsonValueKind.String ? em.GetString() : "";
                            r.Error = $"百度: {code} {msg}";
                            return r;
                        }
                    }
                    r.Src = root.TryGetProperty("src", out var src) && src.ValueKind == JsonValueKind.String ? src.GetString() : "";
                    r.Dst = root.TryGetProperty("dst", out var dst) && dst.ValueKind == JsonValueKind.String ? dst.GetString() : "";
                    if (root.TryGetProperty("paste_img", out var pi) && pi.ValueKind == JsonValueKind.String)
                    {
                        string b64 = pi.GetString();
                        if (!string.IsNullOrEmpty(b64))
                        {
                            try { r.PasteImage = Convert.FromBase64String(b64); } catch { }
                        }
                    }
                }
                if (r.PasteImage == null && string.IsNullOrEmpty(r.Dst))
                    r.Error = "未返回翻译结果（图片可能无文字或识别失败）";
                return r;
            }
            catch (WebException webEx)
            {
                try
                {
                    using (var resp = webEx.Response as HttpWebResponse)
                    {
                        if (resp != null)
                        {
                            using (var rd = new StreamReader(resp.GetResponseStream(), Encoding.UTF8))
                            {
                                var b = rd.ReadToEnd();
                                r.Error = $"HTTP {(int)resp.StatusCode}: {(b.Length > 200 ? b.Substring(0, 200) : b)}";
                                return r;
                            }
                        }
                    }
                }
                catch { }
                r.Error = $"网络错误: {webEx.Message}";
                return r;
            }
            catch (Exception ex) { r.Error = ex.Message; return r; }
        }
    }
}
