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

        // DPAPI entropy binds the encrypted blob to this app. Values are stored as
        // "DP1:" + Base64(ProtectedData(...)) so encrypted and legacy-plaintext values are
        // unambiguous; GetKey transparently reads either.
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
            catch { return plain; } // DPAPI unavailable — store plaintext rather than losing the key
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
                catch { return ""; } // corrupt encrypted blob — don't fall back to garbage
            }
            return stored; // legacy plaintext value saved before DPAPI encryption
        }

        public class Result
        {
            public string Translation;
            public string Error;
            public string DetectedFrom;
        }

        // Baidu AI text translate (aiTextTranslate) enforces a limit on the size of q
        // counted in UTF-8 BYTES (not characters). Exceeding it returns 59003 请求文本太长.
        // CJK chars are 3 bytes each in UTF-8, so a char-based cap is unsafe — we budget
        // by bytes. 4000 bytes/chunk stays well under the limit across endpoints.
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

            // Normalize line endings to '\n' so Windows \r\n carriage returns never reach
            // the API (and don't come back as '\r' in the JSON, which the hand-rolled
            // decoder would otherwise render as a literal 'r' — the source of stray
            // "r" / "-r" fragments on blank lines and lone "-" lines).
            text = text.Replace("\r\n", "\n").Replace("\r", "\n");

            var chunks = SplitIntoChunks(text, MaxChunkBytes);
            if (chunks.Count == 1)
            {
                return TranslateOnce(chunks[0], from, to, appId, key, instruction);
            }

            // Translate each chunk and concatenate. Stop on first hard error.
            // Chunks already carry their trailing separator (space/newline/punctuation),
            // so the translated parts are concatenated directly — never forcing a newline
            // where the original had a space (which produced stray fragments like "r -r r").
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

        // Split text into chunks whose UTF-8 byte length is <= maxBytes. Each emitted chunk
        // keeps the separator that followed it in the source (space, newline, punctuation),
        // so translated chunks can be concatenated with "" and the original spacing survives.
        // Breaks are chosen so a chunk NEVER ends in the middle of a word/identifier — it
        // cuts at a whitespace or word-boundary, hard-wrapping only on CJK runs (where every
        // character is a standalone unit) or an unsplittable single token. Never splits a
        // UTF-16 surrogate pair.
        static List<string> SplitIntoChunks(string text, int maxBytes)
        {
            var chunks = new List<string>();
            if (string.IsNullOrEmpty(text) || ByteLen(text) <= maxBytes)
            {
                chunks.Add(text ?? "");
                return chunks;
            }

            // Walk the whole text accumulating into `cur`; when adding the next token would
            // exceed the byte budget, flush `cur` and start fresh. Tokens here are runs of
            // non-newline characters; newlines are kept as their own tokens so paragraph
            // structure is preserved across chunk boundaries.
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

                // A single token (e.g. one very long line) exceeds the budget on its own —
                // break it into safe pieces. Emit complete pieces directly; keep the
                // trailing remainder in `cur` so the next token can join it if it fits.
                var pieces = SplitLongString(tok, maxBytes);
                for (int i = 0; i < pieces.Count - 1; i++) chunks.Add(pieces[i]);
                cur.Append(pieces[pieces.Count - 1]);
            }
            if (cur.Length > 0) chunks.Add(cur.ToString());
            return chunks;
        }

        // Yield tokens that are either a single newline (kept verbatim) or a run of
        // non-newline characters up to the next newline.
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

        // A character that is part of an ascii word/identifier (letters, digits, hyphen,
        // underscore, apostrophe). Used to avoid splitting a word/identifier in two.
        static bool IsWordChar(char ch)
        {
            return char.IsLetterOrDigit(ch) || ch == '-' || ch == '_' || ch == '\'';
        }

        // Break a single long token (no newlines) into pieces each <= maxBytes UTF-8.
        // Prefers cutting after whitespace, then after a word boundary, hard-wrapping only
        // when the run is unsplittable (pure CJK or a single over-long token). Never cuts
        // inside a UTF-16 surrogate pair.
        static List<string> SplitLongString(string s, int maxBytes)
        {
            var result = new List<string>();
            int start = 0;
            while (start < s.Length)
            {
                if (ByteLen(s.Substring(start)) <= maxBytes) { result.Add(s.Substring(start)); break; }

                // Walk forward accumulating bytes until we hit the budget, never splitting a
                // surrogate pair.
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
                // `i` is the first index that would overflow the budget. Choose a safe cut
                // point `cut` in (start, i] so that we don't end mid-word.
                int cut = ChooseSafeCut(s, start, i);
                result.Add(s.Substring(start, cut - start));
                start = cut;
            }
            return result;
        }

        // Pick the largest cut <= maxIdx that doesn't leave a word/identifier split in two.
        // Preference: cut right after a whitespace char; otherwise cut at a word-boundary
        // (where not both neighbours are word chars); otherwise hard-wrap at maxIdx (only
        // happens for pure CJK runs or a single unsplittable token).
        static int ChooseSafeCut(string s, int start, int maxIdx)
        {
            // 1) Last whitespace at or before maxIdx — cut just after it (keep the space).
            for (int j = maxIdx; j > start; j--)
            {
                if (char.IsWhiteSpace(s[j - 1])) return j;
            }
            // 2) Last word-boundary at or before maxIdx: a position j where it is NOT the
            //    case that both s[j-1] and s[j] are word chars (i.e. we're not slicing
            //    through "over-r" -> "over" / "-r").
            for (int j = maxIdx; j > start; j--)
            {
                bool left = j - 1 >= 0 && IsWordChar(s[j - 1]);
                bool right = j < s.Length && IsWordChar(s[j]);
                if (!(left && right)) return j;
            }
            // 3) No safe boundary (single unsplittable token / pure CJK) — hard wrap.
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

        // Real JSON parsing via System.Text.Json — decodes all standard escapes,
        // nested objects and arrays correctly.
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

        // `result` is a single translated string in the AI translate response, but accept an
        // array of strings (one per input line) and join them on newlines as a safety net.
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

        // Classic trans_result fallback: [{"src":"...","dst":"..."}, ...]
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