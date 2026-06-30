using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
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

        public static string GetAppId()
        {
            try { using (var k = Registry.CurrentUser.OpenSubKey(KeyPath)) return k == null ? "" : (k.GetValue("Translate.AppId") as string ?? ""); }
            catch { return ""; }
        }

        public static string GetKey()
        {
            try { using (var k = Registry.CurrentUser.OpenSubKey(KeyPath)) return UnprotectKey(k == null ? "" : (k.GetValue("Translate.Key") as string ?? "")); }
            catch { return ""; }
        }

        public static string GetInstruction()
        {
            try { using (var k = Registry.CurrentUser.OpenSubKey(KeyPath)) return k == null ? "" : (k.GetValue("Translate.Instruction") as string ?? ""); }
            catch { return ""; }
        }

        public static void SetCreds(string appId, string key, string instruction)
        {
            try
            {
                using (var k = Registry.CurrentUser.CreateSubKey(KeyPath))
                {
                    k.SetValue("Translate.AppId", appId ?? "");
                    k.SetValue("Translate.Key", ProtectKey(key ?? ""));
                    k.SetValue("Translate.Instruction", instruction ?? "");
                }
            }
            catch { }
        }

        static string ProtectKey(string plain)
        {
            if (string.IsNullOrEmpty(plain)) return "";
            try
            {
                var enc = System.Security.Cryptography.ProtectedData.Protect(
                    System.Text.Encoding.UTF8.GetBytes(plain), KeyEntropy,
                    System.Security.Cryptography.DataProtectionScope.CurrentUser);
                return "DP1:" + Convert.ToBase64String(enc);
            }
            catch { return plain; } // DPAPI 不可用 — 存储明文以免丢失密钥
        }

        static string UnprotectKey(string stored)
        {
            if (string.IsNullOrEmpty(stored)) return "";
            if (stored.StartsWith("DP1:"))
            {
                try
                {
                    var enc = Convert.FromBase64String(stored.Substring(4));
                    return System.Text.Encoding.UTF8.GetString(
                        System.Security.Cryptography.ProtectedData.Unprotect(enc, KeyEntropy,
                            System.Security.Cryptography.DataProtectionScope.CurrentUser));
                }
                catch { return ""; } // 加密数据损坏 — 不退回乱码
            }
            return stored; // DPAPI 加密前的遗留明文
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
            var r = new Result();
            string appId = GetAppId();
            string key = GetKey();
            string instruction = GetInstruction();

            if (string.IsNullOrEmpty(key))
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
                return TranslateOnce(chunks[0], from, to, appId, key, instruction);
            }

            // 逐段翻译并拼接。首错即停。分段已保留尾部分隔符（空格/换行/标点），
            // 直接拼接即可，不会在原文空格处强行插入换行（避免产生 "r -r r" 等残余片段）。
            var parts = new List<string>();
            string detected = null;
            for (int i = 0; i < chunks.Count; i++)
            {
                var cr = TranslateOnce(chunks[i], from, to, appId, key, instruction);
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

        static Result TranslateOnce(string text, string from, string to, string appId, string key, string instruction)
        {
            var r = new Result();
            if (string.IsNullOrEmpty(text)) { r.Translation = ""; return r; }
            try
            {
                System.Net.ServicePointManager.SecurityProtocol =
                    System.Net.SecurityProtocolType.Tls12 | System.Net.ServicePointManager.SecurityProtocol;
                var req = (System.Net.HttpWebRequest)System.Net.WebRequest.Create(EndpointAi);
                req.Method = "POST";
                req.ContentType = "application/json";
                req.Headers["Authorization"] = "Bearer " + key;
                req.Timeout = 15000;

                string fromArg = string.IsNullOrEmpty(from) ? "auto" : from;
                string toArg = string.IsNullOrEmpty(to) ? "zh" : to;

                var payload = new Dictionary<string, object>();
                if (!string.IsNullOrEmpty(appId)) payload["appid"] = appId;
                payload["from"] = fromArg;
                payload["to"] = toArg;
                payload["q"] = text;
                if (!string.IsNullOrEmpty(instruction)) payload["instruction"] = instruction;

                var body = JsonSerializer.SerializeToUtf8Bytes(payload);
                req.ContentLength = body.Length;
                using (var s = req.GetRequestStream()) s.Write(body, 0, body.Length);

                using (var resp = (System.Net.HttpWebResponse)req.GetResponse())
                using (var rs = resp.GetResponseStream())
                using (var rd = new StreamReader(rs, System.Text.Encoding.UTF8))
                {
                    var json = rd.ReadToEnd();
                    var root = ParseJson(json);

                    string err = root == null ? null : AsString(root, "error_code");
                    if (!string.IsNullOrEmpty(err) && err != "0" && err != "52000")
                    {
                        r.Error = $"百度: {err} {AsString(root, "error_msg")}";
                        return r;
                    }

                    string result = root == null ? null : ExtractResult(root);
                    if (!string.IsNullOrEmpty(result))
                    {
                        r.Translation = result;
                        r.DetectedFrom = root == null ? null : AsString(root, "from");
                        return r;
                    }

                    var dst = root == null ? null : ExtractDstList(root);
                    if (dst != null && dst.Count > 0)
                    {
                        r.Translation = string.Join(System.Environment.NewLine, dst.ToArray());
                        r.DetectedFrom = root == null ? null : AsString(root, "from");
                        return r;
                    }

                    r.Error = "响应解析失败: " + (json.Length > 200 ? json.Substring(0, 200) + "..." : json);
                }
            }
            catch (System.Net.WebException webEx)
            {
                try
                {
                    using (var resp = webEx.Response as System.Net.HttpWebResponse)
                    {
                        if (resp != null)
                        {
                            using (var rd = new StreamReader(resp.GetResponseStream(), System.Text.Encoding.UTF8))
                            {
                                var b = rd.ReadToEnd();
                                r.Error = "HTTP " + (int)resp.StatusCode + ": " + (b.Length > 200 ? b.Substring(0, 200) : b);
                                return r;
                            }
                        }
                    }
                }
                catch { }
                r.Error = $"网络错误: {webEx.Message}";
            }
            catch (Exception ex)
            {
                r.Error = ex.Message;
            }
            return r;
        }

        static JsonDocument ParseJson(string json)
        {
            if (string.IsNullOrEmpty(json)) return null;
            try { return JsonDocument.Parse(json); }
            catch { return null; }
        }

        static string AsString(JsonDocument d, string key)
        {
            if (d == null) return null;
            return AsString(d.RootElement, key);
        }

        static string AsString(JsonElement el, string key)
        {
            if (el.ValueKind != JsonValueKind.Object) return null;
            if (el.TryGetProperty(key, out var p) && p.ValueKind == JsonValueKind.String)
                return p.GetString();
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