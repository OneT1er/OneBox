using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Win32;

namespace PowerAudioManager
{
    public static class TranslateService
    {
        const string EndpointAi = "https://fanyi-api.baidu.com/ait/api/aiTextTranslate";
        const string KeyPath = @"Software\PowerAudioManager\App";

        // DPAPI 熵绑定加密数据到本应用。存储格式 "DP1:" + Base64(ProtectedData(...))，
        // 加密值与遗留明文可明确区分，GetKey 透明读取。
        static readonly byte[] KeyEntropy = System.Text.Encoding.UTF8.GetBytes("OneBox.Translate.Key.v1");
        static readonly object CredentialErrorLock = new object();
        static string _credentialError;

        public static string GetAppId()
        {
            try { using (var k = Registry.CurrentUser.OpenSubKey(KeyPath)) return k == null ? "" : (k.GetValue("Translate.AppId") as string ?? ""); }
            catch { return ""; }
        }

        public static string GetKey()
        {
            SetCredentialError(null);
            string stored;
            try
            {
                using var key = Registry.CurrentUser.OpenSubKey(KeyPath);
                stored = key == null ? "" : key.GetValue("Translate.Key") as string ?? "";
            }
            catch (Exception ex)
            {
                SetCredentialError("无法读取翻译 API Key：" + ex.Message);
                return "";
            }

            if (!TryUnprotectKeyFromStorage(stored, UnprotectWithDpapi, out var plain, out var legacy, out var error))
            {
                SetCredentialError(error);
                return "";
            }
            if (!legacy || string.IsNullOrEmpty(plain)) return plain;

            // 遗留明文先加密成功，再原位替换；任一步失败都保留原值。
            if (!TryProtectKeyForStorage(plain, ProtectWithDpapi, out var protectedValue, out error))
            {
                SetCredentialError("API Key 安全迁移失败，原值已保留：" + error);
                return plain;
            }
            try
            {
                using var key = Registry.CurrentUser.CreateSubKey(KeyPath);
                key.SetValue("Translate.Key", protectedValue);
            }
            catch (Exception ex)
            {
                SetCredentialError("API Key 安全迁移失败，原值已保留：" + ex.Message);
            }
            return plain;
        }

        public static string GetInstruction()
        {
            try { using (var k = Registry.CurrentUser.OpenSubKey(KeyPath)) return k == null ? "" : (k.GetValue("Translate.Instruction") as string ?? ""); }
            catch { return ""; }
        }

        public static bool SetCreds(string appId, string key, string instruction)
        {
            SetCredentialError(null);
            if (!TryProtectKeyForStorage(key ?? "", ProtectWithDpapi, out var protectedKey, out var error))
            {
                SetCredentialError("API Key 加密失败，设置未保存：" + error);
                return false;
            }
            try
            {
                using (var k = Registry.CurrentUser.CreateSubKey(KeyPath))
                {
                    k.SetValue("Translate.AppId", appId ?? "");
                    k.SetValue("Translate.Key", protectedKey);
                    k.SetValue("Translate.Instruction", instruction ?? "");
                }
                return true;
            }
            catch (Exception ex)
            {
                SetCredentialError("翻译设置保存失败：" + ex.Message);
                return false;
            }
        }

        public static string GetCredentialError()
        {
            lock (CredentialErrorLock) return _credentialError;
        }

        static void SetCredentialError(string error)
        {
            lock (CredentialErrorLock) _credentialError = error;
        }

        static byte[] ProtectWithDpapi(byte[] plain)
        {
            return System.Security.Cryptography.ProtectedData.Protect(
                plain, KeyEntropy, System.Security.Cryptography.DataProtectionScope.CurrentUser);
        }

        static byte[] UnprotectWithDpapi(byte[] encrypted)
        {
            return System.Security.Cryptography.ProtectedData.Unprotect(
                encrypted, KeyEntropy, System.Security.Cryptography.DataProtectionScope.CurrentUser);
        }

        // 公开为无注册表、无真实 DPAPI 的纯逻辑测试入口。
        public static bool TryProtectKeyForStorage(
            string plain, Func<byte[], byte[]> protector, out string stored, out string error)
        {
            stored = "";
            error = null;
            if (string.IsNullOrEmpty(plain)) return true;
            if (protector == null)
            {
                error = "没有可用的凭据保护器";
                return false;
            }
            try
            {
                byte[] encrypted = protector(Encoding.UTF8.GetBytes(plain));
                if (encrypted == null || encrypted.Length == 0) throw new InvalidOperationException("保护器返回空数据");
                stored = "DP1:" + Convert.ToBase64String(encrypted);
                return true;
            }
            catch (Exception ex)
            {
                error = ex.Message;
                return false;
            }
        }

        public static bool TryUnprotectKeyFromStorage(
            string stored, Func<byte[], byte[]> unprotector, out string plain, out bool legacy, out string error)
        {
            plain = "";
            legacy = false;
            error = null;
            if (string.IsNullOrEmpty(stored)) return true;
            if (!stored.StartsWith("DP1:", StringComparison.Ordinal))
            {
                plain = stored;
                legacy = true;
                return true;
            }
            if (unprotector == null)
            {
                error = "没有可用的凭据解密器";
                return false;
            }
            try
            {
                byte[] decrypted = unprotector(Convert.FromBase64String(stored.Substring(4)));
                if (decrypted == null) throw new InvalidOperationException("解密器返回空数据");
                plain = Encoding.UTF8.GetString(decrypted);
                return true;
            }
            catch (Exception ex)
            {
                error = "API Key 解密失败：" + ex.Message;
                return false;
            }
        }

        public class Result
        {
            public string Translation;
            public string Error;
            public string DetectedFrom;
        }

        // 百度 AI 文本翻译对 q 有 UTF-8 字节数限制（非字符数），超限返回 59003。
        // CJK 字符在 UTF-8 中每字 3 字节，按字符数截断不可靠 — 按字节预算。4000 字节/段在各端点都在安全范围内。
        const int MaxChunkBytes = 4000;
        static readonly System.Text.Encoding Utf8 = System.Text.Encoding.UTF8;

        static int ByteLen(string s)
        {
            return s == null ? 0 : Utf8.GetByteCount(s);
        }

        public static Result Translate(string text, string from, string to)
        {
            return TranslateAsync(text, from, to, CancellationToken.None).GetAwaiter().GetResult();
        }

        public static async Task<Result> TranslateAsync(
            string text, string from, string to, CancellationToken cancellationToken)
        {
            string appId = GetAppId();
            string key = GetKey();
            string instruction = GetInstruction();
            string credentialError = GetCredentialError();
            if (!string.IsNullOrEmpty(credentialError)) return new Result { Error = credentialError };
            return await TranslateAsync(
                text, from, to, appId, key, instruction, OneBoxHttp.Client, cancellationToken).ConfigureAwait(false);
        }

        public static async Task<Result> TranslateAsync(
            string text,
            string from,
            string to,
            string appId,
            string apiKey,
            string instruction,
            HttpClient httpClient,
            CancellationToken cancellationToken)
        {
            var r = new Result();
            if (string.IsNullOrEmpty(apiKey))
            {
                r.Error = "未设置 API Key（点击翻译窗口的设置按钮配置）";
                return r;
            }
            if (string.IsNullOrEmpty(text)) { r.Translation = ""; return r; }

            // 统一换行为 '\n'，避免 Windows \r\n 传到 API 后返回 JSON 中的 '\r' 被渲染为字面 'r'
            // — 这是空行和单独 "-" 行出现残余 "r" / "-r" 片段的根源。
            text = text.Replace("\r\n", "\n").Replace("\r", "\n");

            var chunks = SplitIntoChunks(text, MaxChunkBytes);
            if (chunks.Count == 1)
            {
                return await TranslateOnceAsync(
                    chunks[0], from, to, appId, apiKey, instruction, httpClient, cancellationToken).ConfigureAwait(false);
            }

            // 逐段翻译并拼接。首错即停。分段已保留尾部分隔符（空格/换行/标点），
            // 直接拼接即可，不会在原文空格处强行插入换行（避免产生 "r -r r" 等残余片段）。
            var parts = new List<string>();
            string detected = null;
            for (int i = 0; i < chunks.Count; i++)
            {
                if (cancellationToken.IsCancellationRequested)
                    return new Result { Error = "请求已取消" };
                var cr = await TranslateOnceAsync(
                    chunks[i], from, to, appId, apiKey, instruction, httpClient, cancellationToken).ConfigureAwait(false);
                if (!string.IsNullOrEmpty(cr.Error))
                {
                    r.Error = $"第 {i + 1}/{chunks.Count} 段失败: {cr.Error}";
                    return r;
                }
                if (detected == null) detected = cr.DetectedFrom;
                parts.Add(cr.Translation ?? "");
            }
            r.Translation = string.Join("", parts.ToArray());
            r.DetectedFrom = detected;
            return r;
        }

        // 将文本按 UTF-8 字节数分段（≤ maxBytes）。每段保留其后的分隔符，使译文可直接用 "" 拼接。
        // 切分规则：绝不在单词/标识符中间截断，优先切在空白或单词边界。仅纯 CJK 或单个超长 token
        // 才强制截断。不分割 UTF-16 代理对。
        static List<string> SplitIntoChunks(string text, int maxBytes)
        {
            var chunks = new List<string>();
            if (string.IsNullOrEmpty(text) || ByteLen(text) <= maxBytes)
            {
                chunks.Add(text ?? "");
                return chunks;
            }

            // 遍历文本累积到 cur；若加入下一个 token 会超字节预算则刷出 cur。
            // token 为非换行字符序列，换行符单独保留以维持段落结构。
            var cur = new System.Text.StringBuilder();
            foreach (var tok in TokenizeKeepNewlines(text))
            {
                if (cur.Length > 0 && ByteLen(cur.ToString()) + ByteLen(tok) > maxBytes)
                {
                    chunks.Add(cur.ToString());
                    cur.Length = 0;
                }

                if (ByteLen(tok) <= maxBytes)
                {
                    cur.Append(tok);
                    continue;
                }

                // 单个 token 超长 — 拆分为安全片段。完整片段直接输出，
                // 尾部余量留在 cur 中以便与下一个 token 合并。
                var pieces = SplitLongString(tok, maxBytes);
                for (int i = 0; i < pieces.Count - 1; i++) chunks.Add(pieces[i]);
                cur.Append(pieces[pieces.Count - 1]);
            }
            if (cur.Length > 0) chunks.Add(cur.ToString());
            return chunks;
        }

        static IEnumerable<string> TokenizeKeepNewlines(string text)
        {
            int start = 0;
            for (int i = 0; i < text.Length; i++)
            {
                if (text[i] == '\n')
                {
                    if (i > start) yield return text.Substring(start, i - start);
                    yield return "\n";
                    start = i + 1;
                }
            }
            if (start < text.Length) yield return text.Substring(start);
        }

        static bool IsWordChar(char ch)
        {
            return char.IsLetterOrDigit(ch) || ch == '-' || ch == '_' || ch == '\'';
        }

        // 拆分超长 token（无换行）为 ≤ maxBytes 的片段。优先切在空白后，其次单词边界。
        // 仅在纯 CJK 或单个不可拆分 token 时强制截断。不分割 UTF-16 代理对。
        static List<string> SplitLongString(string s, int maxBytes)
        {
            var result = new List<string>();
            int start = 0;
            while (start < s.Length)
            {
                if (ByteLen(s.Substring(start)) <= maxBytes) { result.Add(s.Substring(start)); break; }

                int i = start;
                int bytes = 0;
                while (i < s.Length)
                {
                    int c = char.IsSurrogatePair(s, i) ? 2 : 1;
                    int add = ByteLen(s.Substring(i, c));
                    if (bytes + add > maxBytes) break;
                    bytes += add;
                    i += c;
                }
                int cut = ChooseSafeCut(s, start, i);
                result.Add(s.Substring(start, cut - start));
                start = cut;
            }
            return result;
        }

        // 选择 ≤ maxIdx 的最大安全切点：优先切在空白之后，其次单词边界，
        // 最后才在 maxIdx 强制截断（纯 CJK 或不可拆分 token）。
        static int ChooseSafeCut(string s, int start, int maxIdx)
        {
            // 1) 找 maxIdx 或之前最后一个空白，切在它之后（保留该空白）。
            for (int j = maxIdx; j > start; j--)
            {
                if (char.IsWhiteSpace(s[j - 1])) return j;
            }
            // 2) 找 maxIdx 或之前最后一个单词边界：j 处不能同时是单词字符（避免把 "over-r" 切成 "over" / "-r"）。
            for (int j = maxIdx; j > start; j--)
            {
                bool left = j - 1 >= 0 && IsWordChar(s[j - 1]);
                bool right = j < s.Length && IsWordChar(s[j]);
                if (!(left && right)) return j;
            }
            // 3) 无安全边界（单个不可拆分 token / 纯 CJK）— 强制截断。
            return maxIdx;
        }

        static async Task<Result> TranslateOnceAsync(
            string text,
            string from,
            string to,
            string appId,
            string key,
            string instruction,
            HttpClient httpClient,
            CancellationToken cancellationToken)
        {
            var r = new Result();
            if (string.IsNullOrEmpty(text)) { r.Translation = ""; return r; }
            try
            {
                string fromArg = string.IsNullOrEmpty(from) ? "auto" : from;
                string toArg = string.IsNullOrEmpty(to) ? "zh" : to;

                var payload = new Dictionary<string, object>();
                if (!string.IsNullOrEmpty(appId)) payload["appid"] = appId;
                payload["from"] = fromArg;
                payload["to"] = toArg;
                payload["q"] = text;
                if (!string.IsNullOrEmpty(instruction)) payload["instruction"] = instruction;

                using var request = new HttpRequestMessage(HttpMethod.Post, EndpointAi);
                request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", key);
                request.Headers.UserAgent.ParseAdd("OneBox/" + ApplicationVersion.Value);
                request.Headers.Accept.ParseAdd("application/json");
                request.Content = new ByteArrayContent(JsonSerializer.SerializeToUtf8Bytes(payload));
                request.Content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("application/json");
                string json = await OneBoxHttp.SendForTextAsync(
                    httpClient, request, TimeSpan.FromSeconds(15), cancellationToken).ConfigureAwait(false);

                try
                {
                    using var root = JsonDocument.Parse(json);

                    string err = AsString(root, "error_code");
                    if (!string.IsNullOrEmpty(err) && err != "0" && err != "52000")
                    {
                        r.Error = $"百度: {err} {AsString(root, "error_msg")}";
                        return r;
                    }

                    string result = ExtractResult(root);
                    if (!string.IsNullOrEmpty(result))
                    {
                        r.Translation = result;
                        r.DetectedFrom = AsString(root, "from");
                        return r;
                    }

                    var dst = ExtractDstList(root);
                    if (dst != null && dst.Count > 0)
                    {
                        r.Translation = string.Join(System.Environment.NewLine, dst.ToArray());
                        r.DetectedFrom = AsString(root, "from");
                        return r;
                    }

                    r.Error = "服务响应中没有翻译结果";
                }
                catch (JsonException ex)
                {
                    r.Error = "服务响应格式无效：" + ex.Message;
                }
            }
            catch (OneBoxHttpException ex)
            {
                r.Error = ex.Message;
            }
            catch (Exception ex)
            {
                AppLog.Log("TranslateService", ex);
                r.Error = "翻译失败：" + ex.Message;
            }
            return r;
        }

        static string AsString(JsonDocument d, string key)
        {
            if (d == null) return null;
            return AsString(d.RootElement, key);
        }

        static string AsString(JsonElement el, string key)
        {
            if (el.ValueKind != JsonValueKind.Object) return null;
            if (el.TryGetProperty(key, out var p))
            {
                if (p.ValueKind == JsonValueKind.String) return p.GetString();
                if (p.ValueKind == JsonValueKind.Number) return p.ToString();
            }
            return null;
        }

        // result 为 AI 翻译响应中的译文，兼容字符串和数组（按行拼接）。
        static string ExtractResult(JsonDocument d)
        {
            if (d == null) return null;
            return ExtractResult(d.RootElement);
        }

        static string ExtractResult(JsonElement el)
        {
            if (el.ValueKind != JsonValueKind.Object) return null;
            if (!el.TryGetProperty("result", out var v)) return null;
            if (v.ValueKind == JsonValueKind.String) return v.GetString();
            if (v.ValueKind == JsonValueKind.Array)
            {
                var sb = new System.Text.StringBuilder();
                bool first = true;
                foreach (var item in v.EnumerateArray())
                {
                    if (item.ValueKind == JsonValueKind.Null) continue;
                    if (!first) sb.Append(System.Environment.NewLine);
                    sb.Append(item.ValueKind == JsonValueKind.String ? item.GetString() : item.ToString());
                    first = false;
                }
                return sb.ToString();
            }
            return v.ToString();
        }

        // 经典 trans_result 回退格式: [{"src":"...","dst":"..."}, ...]
        static List<string> ExtractDstList(JsonDocument d)
        {
            var list = new List<string>();
            if (d == null) return list;
            if (d.RootElement.ValueKind != JsonValueKind.Object) return list;
            if (!d.RootElement.TryGetProperty("trans_result", out var arr)) return list;
            if (arr.ValueKind != JsonValueKind.Array) return list;
            foreach (var item in arr.EnumerateArray())
            {
                if (item.ValueKind != JsonValueKind.Object) continue;
                if (item.TryGetProperty("dst", out var dst) && dst.ValueKind == JsonValueKind.String)
                    list.Add(dst.GetString());
            }
            return list;
        }
    }
}
