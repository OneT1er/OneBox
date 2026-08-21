using System;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace PowerAudioManager
{
    public sealed class OneBoxHttpException : Exception
    {
        public OneBoxHttpException(string message, Exception innerException = null)
            : base(message, innerException)
        {
        }
    }

    // 异步 UI 请求的代次门：只允许最后一次提交写回界面。
    public sealed class RequestGenerationGate
    {
        int _generation;

        public int Begin() => Interlocked.Increment(ref _generation);

        public bool IsCurrent(int generation) => Volatile.Read(ref _generation) == generation;

        public void Invalidate() => Interlocked.Increment(ref _generation);
    }

    // 全应用共享连接池。具体服务仍可注入 HttpClient，以便用自定义 handler 做离线测试。
    public static class OneBoxHttp
    {
        static readonly HttpClient SharedClient = CreateSharedClient();

        public static HttpClient Client => SharedClient;

        // ResponseHeadersRead avoids buffering untrusted response bodies in
        // HttpClient. Callers that handle user-supplied URLs must use these
        // bounded readers before converting a response to a byte array/string.
        public static async Task<byte[]> ReadBoundedBytesAsync(HttpContent content,
            int maxBytes, CancellationToken cancellationToken)
        {
            if (content == null) throw new ArgumentNullException(nameof(content));
            if (maxBytes <= 0) throw new ArgumentOutOfRangeException(nameof(maxBytes));
            long? contentLength = content.Headers.ContentLength;
            if (contentLength.HasValue && contentLength.Value > maxBytes)
                throw new OneBoxHttpException("网络响应超过大小限制");

            await using var source = await content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
            using var buffer = new MemoryStream(contentLength.HasValue
                ? (int)contentLength.Value
                : Math.Min(maxBytes, 4096));
            var chunk = new byte[Math.Min(maxBytes, 81920)];
            int total = 0;
            int read;
            while ((read = await source.ReadAsync(chunk.AsMemory(0, chunk.Length), cancellationToken).ConfigureAwait(false)) > 0)
            {
                if (read > maxBytes - total)
                    throw new OneBoxHttpException("网络响应超过大小限制");
                await buffer.WriteAsync(chunk.AsMemory(0, read), cancellationToken).ConfigureAwait(false);
                total += read;
            }
            return buffer.ToArray();
        }

        public static async Task<string> ReadBoundedTextAsync(HttpContent content,
            int maxBytes, CancellationToken cancellationToken)
        {
            byte[] bytes = await ReadBoundedBytesAsync(content, maxBytes, cancellationToken).ConfigureAwait(false);
            Encoding encoding = Encoding.UTF8;
            string charset = content.Headers.ContentType?.CharSet;
            if (!string.IsNullOrWhiteSpace(charset))
            {
                try { encoding = Encoding.GetEncoding(charset.Trim('"')); }
                catch (ArgumentException) { }
            }
            return encoding.GetString(bytes);
        }

        static HttpClient CreateSharedClient()
        {
            var handler = new SocketsHttpHandler
            {
                AllowAutoRedirect = true,
                AutomaticDecompression = DecompressionMethods.All,
                PooledConnectionLifetime = TimeSpan.FromMinutes(10)
            };
            return new HttpClient(handler, true) { Timeout = Timeout.InfiniteTimeSpan };
        }

        public static async Task<string> SendForTextAsync(
            HttpClient client,
            HttpRequestMessage request,
            TimeSpan timeout,
            CancellationToken cancellationToken)
        {
            if (client == null) throw new ArgumentNullException(nameof(client));
            if (request == null) throw new ArgumentNullException(nameof(request));

            using var timeoutSource = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeoutSource.CancelAfter(timeout);
            try
            {
                using var response = await client.SendAsync(
                    request, HttpCompletionOption.ResponseHeadersRead, timeoutSource.Token).ConfigureAwait(false);
                string body = await response.Content.ReadAsStringAsync(timeoutSource.Token).ConfigureAwait(false);
                EnsureSuccess(response, body);
                if (string.IsNullOrWhiteSpace(body))
                    throw new OneBoxHttpException("服务返回空响应");
                return body;
            }
            catch (OperationCanceledException ex) when (cancellationToken.IsCancellationRequested)
            {
                throw new OneBoxHttpException("请求已取消", ex);
            }
            catch (OperationCanceledException ex)
            {
                throw new OneBoxHttpException("请求超时，请稍后重试", ex);
            }
            catch (HttpRequestException ex)
            {
                throw new OneBoxHttpException("网络请求失败：" + ex.Message, ex);
            }
            catch (IOException ex)
            {
                throw new OneBoxHttpException("网络响应读取失败：" + ex.Message, ex);
            }
        }

        public static async Task DownloadFileAsync(
            HttpClient client,
            HttpRequestMessage request,
            string destinationPath,
            TimeSpan timeout,
            Action<long> progressCallback,
            CancellationToken cancellationToken)
        {
            if (client == null) throw new ArgumentNullException(nameof(client));
            if (request == null) throw new ArgumentNullException(nameof(request));
            if (string.IsNullOrWhiteSpace(destinationPath)) throw new ArgumentException("下载目标路径不能为空", nameof(destinationPath));

            using var timeoutSource = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeoutSource.CancelAfter(timeout);
            try
            {
                using var response = await client.SendAsync(
                    request, HttpCompletionOption.ResponseHeadersRead, timeoutSource.Token).ConfigureAwait(false);
                if (!response.IsSuccessStatusCode)
                {
                    string body = await response.Content.ReadAsStringAsync(timeoutSource.Token).ConfigureAwait(false);
                    EnsureSuccess(response, body);
                }

                await using var source = await response.Content.ReadAsStreamAsync(timeoutSource.Token).ConfigureAwait(false);
                await using var destination = new FileStream(
                    destinationPath, FileMode.CreateNew, FileAccess.Write, FileShare.None, 81920, true);
                var buffer = new byte[81920];
                long total = 0;
                int read;
                while ((read = await source.ReadAsync(buffer.AsMemory(0, buffer.Length), timeoutSource.Token).ConfigureAwait(false)) > 0)
                {
                    await destination.WriteAsync(buffer.AsMemory(0, read), timeoutSource.Token).ConfigureAwait(false);
                    total += read;
                    progressCallback?.Invoke(total);
                }
                await destination.FlushAsync(timeoutSource.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException ex) when (cancellationToken.IsCancellationRequested)
            {
                throw new OneBoxHttpException("请求已取消", ex);
            }
            catch (OperationCanceledException ex)
            {
                throw new OneBoxHttpException("请求超时，请稍后重试", ex);
            }
            catch (HttpRequestException ex)
            {
                throw new OneBoxHttpException("网络请求失败：" + ex.Message, ex);
            }
            catch (IOException ex)
            {
                throw new OneBoxHttpException("网络响应读取失败：" + ex.Message, ex);
            }
        }

        static void EnsureSuccess(HttpResponseMessage response, string body)
        {
            if (response.IsSuccessStatusCode) return;
            int status = (int)response.StatusCode;
            if (response.StatusCode == HttpStatusCode.TooManyRequests)
                throw new OneBoxHttpException("请求过于频繁（HTTP 429），请稍后重试");

            string detail = Compact(body, 200);
            string suffix = string.IsNullOrEmpty(detail) ? "" : "：" + detail;
            throw new OneBoxHttpException($"服务返回 HTTP {status}{suffix}");
        }

        static string Compact(string value, int maxLength)
        {
            if (string.IsNullOrWhiteSpace(value)) return "";
            string compact = value.Replace('\r', ' ').Replace('\n', ' ').Trim();
            return compact.Length <= maxLength ? compact : compact.Substring(0, maxLength) + "…";
        }
    }
}
