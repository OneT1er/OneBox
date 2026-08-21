using System;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using PowerAudioManager;
using Xunit;

namespace OneBox.Tests;

public sealed class HttpClientTests
{
    [Fact]
    public async Task EmptyResponse_IsReportedClearly()
    {
        using var client = TestHttp.Create((_, _) => TestHttp.Json(""));
        using var request = new HttpRequestMessage(HttpMethod.Get, "https://example.test/");

        var error = await Assert.ThrowsAsync<OneBoxHttpException>(() =>
            OneBoxHttp.SendForTextAsync(client, request, TimeSpan.FromSeconds(1), CancellationToken.None));

        Assert.Contains("空响应", error.Message);
    }

    [Fact]
    public async Task RateLimit_IsReportedClearly()
    {
        using var client = TestHttp.Create((_, _) =>
            new HttpResponseMessage(HttpStatusCode.TooManyRequests) { Content = new StringContent("slow down") });
        using var request = new HttpRequestMessage(HttpMethod.Get, "https://example.test/");

        var error = await Assert.ThrowsAsync<OneBoxHttpException>(() =>
            OneBoxHttp.SendForTextAsync(client, request, TimeSpan.FromSeconds(1), CancellationToken.None));

        Assert.Contains("HTTP 429", error.Message);
        Assert.Contains("稍后重试", error.Message);
    }

    [Fact]
    public async Task Timeout_IsDistinctFromCallerCancellation()
    {
        using var client = TestHttp.Create(async (_, token) =>
        {
            await Task.Delay(Timeout.InfiniteTimeSpan, token);
            return TestHttp.Json("{}");
        });
        using var request = new HttpRequestMessage(HttpMethod.Get, "https://example.test/");

        var error = await Assert.ThrowsAsync<OneBoxHttpException>(() =>
            OneBoxHttp.SendForTextAsync(client, request, TimeSpan.FromMilliseconds(20), CancellationToken.None));

        Assert.Contains("请求超时", error.Message);
    }

    [Fact]
    public void GenerationGate_RejectsAnOlderResponse()
    {
        var gate = new RequestGenerationGate();
        int first = gate.Begin();
        int second = gate.Begin();

        Assert.False(gate.IsCurrent(first));
        Assert.True(gate.IsCurrent(second));
    }
}

internal sealed class DelegateHandler : HttpMessageHandler
{
    readonly Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> _handler;

    public DelegateHandler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> handler)
    {
        _handler = handler;
    }

    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        return _handler(request, cancellationToken);
    }
}

internal static class TestHttp
{
    public static HttpClient Create(Func<HttpRequestMessage, CancellationToken, HttpResponseMessage> handler)
    {
        return Create((request, token) => Task.FromResult(handler(request, token)));
    }

    public static HttpClient Create(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> handler)
    {
        return new HttpClient(new DelegateHandler(handler)) { Timeout = Timeout.InfiniteTimeSpan };
    }

    public static HttpResponseMessage Json(string json, HttpStatusCode status = HttpStatusCode.OK)
    {
        return new HttpResponseMessage(status)
        {
            Content = new StringContent(json, Encoding.UTF8, "application/json")
        };
    }
}
