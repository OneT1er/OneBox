using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace PowerAudioManager
{
    // 百度图片翻译 API。OCR 识别图中文字，返回 paste=1 整图贴合图及完整译文。
    public static class ImageTranslateService
    {
        const string Endpoint = "https://fanyi-api.baidu.com/ait/api/picture/translate";

        public class ImageResult
        {
            public byte[] PasteImage;
            public string Dst;
            public string Src;
            public string Error;
        }

        public static ImageResult Translate(byte[] imageBytes, string from, string to)
        {
            return TranslateAsync(imageBytes, from, to, CancellationToken.None).GetAwaiter().GetResult();
        }

        public static async Task<ImageResult> TranslateAsync(
            byte[] imageBytes, string from, string to, CancellationToken cancellationToken)
        {
            string appId = TranslateService.GetAppId();
            string key = TranslateService.GetKey();
            string credentialError = TranslateService.GetCredentialError();
            if (!string.IsNullOrEmpty(credentialError)) return new ImageResult { Error = credentialError };
            return await TranslateAsync(
                imageBytes, from, to, appId, key, OneBoxHttp.Client, cancellationToken).ConfigureAwait(false);
        }

        // 可注入 HttpClient 的无联网测试入口；AppId 可选，API Key 必填。
        public static async Task<ImageResult> TranslateAsync(
            byte[] imageBytes,
            string from,
            string to,
            string appId,
            string apiKey,
            HttpClient httpClient,
            CancellationToken cancellationToken)
        {
            var result = new ImageResult();
            if (imageBytes == null || imageBytes.Length == 0) return new ImageResult { Error = "无图片" };
            if (imageBytes.Length > 5 * 1024 * 1024) return new ImageResult { Error = "图片超过 5MB 上限" };
            if (string.IsNullOrEmpty(apiKey))
                return new ImageResult { Error = "未设置 API Key（点击翻译窗口的设置按钮配置）" };

            try
            {
                string fromArg = string.IsNullOrEmpty(from) ? "auto" : from;
                string toArg = string.IsNullOrEmpty(to) ? "zh" : to;
                var payload = new Dictionary<string, object>
                {
                    ["from"] = fromArg,
                    ["to"] = toArg,
                    ["content"] = Convert.ToBase64String(imageBytes),
                    ["paste"] = 1,
                    ["need_intervene"] = 0,
                    ["view_type"] = 0,
                    ["model_type"] = "nmt"
                };
                if (!string.IsNullOrEmpty(appId)) payload["appid"] = appId;

                using var request = new HttpRequestMessage(HttpMethod.Post, Endpoint);
                request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);
                request.Headers.UserAgent.ParseAdd("OneBox/" + ApplicationVersion.Value);
                request.Headers.Accept.ParseAdd("application/json");
                request.Content = new ByteArrayContent(JsonSerializer.SerializeToUtf8Bytes(payload));
                request.Content.Headers.ContentType = new MediaTypeHeaderValue("application/json");
                string json = await OneBoxHttp.SendForTextAsync(
                    httpClient, request, TimeSpan.FromSeconds(30), cancellationToken).ConfigureAwait(false);

                try
                {
                    using var document = JsonDocument.Parse(json);
                    JsonElement root = document.RootElement;
                    string errorCode = ReadString(root, "error_code");
                    if (!string.IsNullOrEmpty(errorCode) && errorCode != "0" && errorCode != "52000")
                    {
                        result.Error = $"百度: {errorCode} {ReadString(root, "error_msg")}";
                        return result;
                    }

                    result.Src = ReadString(root, "src") ?? "";
                    result.Dst = ReadString(root, "dst") ?? "";
                    string base64Image = ReadString(root, "paste_img");
                    if (!string.IsNullOrEmpty(base64Image))
                    {
                        try { result.PasteImage = Convert.FromBase64String(base64Image); }
                        catch (FormatException ex)
                        {
                            result.Error = "服务返回的贴合图片格式无效：" + ex.Message;
                            return result;
                        }
                    }
                }
                catch (JsonException ex)
                {
                    result.Error = "服务响应格式无效：" + ex.Message;
                    return result;
                }

                if (result.PasteImage == null && string.IsNullOrEmpty(result.Dst))
                    result.Error = "未返回翻译结果（图片可能无文字或识别失败）";
                return result;
            }
            catch (OneBoxHttpException ex)
            {
                result.Error = ex.Message;
                return result;
            }
            catch (Exception ex)
            {
                AppLog.Log("ImageTranslateService", ex);
                result.Error = "图片翻译失败：" + ex.Message;
                return result;
            }
        }

        static string ReadString(JsonElement root, string propertyName)
        {
            if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty(propertyName, out var value)) return null;
            if (value.ValueKind == JsonValueKind.String) return value.GetString();
            if (value.ValueKind == JsonValueKind.Number) return value.ToString();
            return null;
        }
    }
}
