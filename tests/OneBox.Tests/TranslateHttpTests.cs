using System;
using System.Net;
using System.Net.Http;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using PowerAudioManager;
using Xunit;

namespace OneBox.Tests;

public sealed class TranslateHttpTests
{
    [Fact]
    public async Task TextTranslation_UsesPostBearerAndOptionalAppId()
    {
        HttpMethod method = null;
        string authorization = null;
        string userAgent = null;
        string body = null;
        using var client = TestHttp.Create(async (request, token) =>
        {
            method = request.Method;
            authorization = request.Headers.Authorization?.ToString();
            userAgent = request.Headers.UserAgent.ToString();
            body = await request.Content.ReadAsStringAsync(token);
            return TestHttp.Json("{\"result\":\"你好\",\"from\":\"en\"}");
        });

        var result = await TranslateService.TranslateAsync(
            "hello", "en", "zh", "", "secret", "", client, CancellationToken.None);

        Assert.Equal(HttpMethod.Post, method);
        Assert.Equal("Bearer secret", authorization);
        Assert.Contains("OneBox/", userAgent);
        using var payload = JsonDocument.Parse(body);
        Assert.False(payload.RootElement.TryGetProperty("appid", out _));
        Assert.Equal("hello", payload.RootElement.GetProperty("q").GetString());
        Assert.Equal("你好", result.Translation);
    }

    [Fact]
    public async Task TextTranslation_MapsHttpFailure()
    {
        using var client = TestHttp.Create((_, _) => TestHttp.Json("denied", HttpStatusCode.Forbidden));

        var result = await TranslateService.TranslateAsync(
            "hello", "en", "zh", "", "secret", "", client, CancellationToken.None);

        Assert.Contains("HTTP 403", result.Error);
    }

    [Fact]
    public async Task TextTranslation_MapsMalformedJson()
    {
        using var client = TestHttp.Create((_, _) => TestHttp.Json("{not-json"));

        var result = await TranslateService.TranslateAsync(
            "hello", "en", "zh", "", "secret", "", client, CancellationToken.None);

        Assert.Contains("响应格式无效", result.Error);
    }

    [Fact]
    public async Task TextTranslation_MapsCallerCancellation()
    {
        using var client = TestHttp.Create(async (_, token) =>
        {
            await Task.Delay(Timeout.InfiniteTimeSpan, token);
            return TestHttp.Json("{}");
        });
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        var result = await TranslateService.TranslateAsync(
            "hello", "en", "zh", "", "secret", "", client, cancellation.Token);

        Assert.Equal("请求已取消", result.Error);
    }

    [Fact]
    public async Task ImageTranslation_AllowsMissingAppIdAndKeepsProtocolFields()
    {
        string body = null;
        string authorization = null;
        using var client = TestHttp.Create(async (request, token) =>
        {
            authorization = request.Headers.Authorization?.ToString();
            body = await request.Content.ReadAsStringAsync(token);
            return TestHttp.Json("{\"dst\":\"完成\"}");
        });

        var result = await ImageTranslateService.TranslateAsync(
            new byte[] { 1, 2, 3 }, "auto", "zh", "", "secret", client, CancellationToken.None);

        Assert.Null(result.Error);
        Assert.Equal("Bearer secret", authorization);
        using var payload = JsonDocument.Parse(body);
        Assert.False(payload.RootElement.TryGetProperty("appid", out _));
        Assert.Equal(1, payload.RootElement.GetProperty("paste").GetInt32());
        Assert.Equal("nmt", payload.RootElement.GetProperty("model_type").GetString());
    }

    [Fact]
    public async Task ImageTranslation_RejectsMalformedPasteImage()
    {
        using var client = TestHttp.Create((_, _) => TestHttp.Json("{\"paste_img\":\"%%%\",\"dst\":\"ok\"}"));

        var result = await ImageTranslateService.TranslateAsync(
            new byte[] { 1 }, "auto", "zh", "app", "secret", client, CancellationToken.None);

        Assert.Contains("贴合图片格式无效", result.Error);
    }

    [Fact]
    public void CredentialProtectionFailure_NeverReturnsPlaintextStorage()
    {
        bool ok = TranslateService.TryProtectKeyForStorage(
            "top-secret", _ => throw new InvalidOperationException("DPAPI unavailable"), out var stored, out var error);

        Assert.False(ok);
        Assert.Equal("", stored);
        Assert.DoesNotContain("top-secret", stored);
        Assert.Contains("DPAPI unavailable", error);
    }

    [Fact]
    public void LegacyCredential_IsReadableAndMarkedForMigration()
    {
        bool ok = TranslateService.TryUnprotectKeyFromStorage(
            "legacy-secret", bytes => bytes, out var plain, out var legacy, out var error);

        Assert.True(ok);
        Assert.True(legacy);
        Assert.Equal("legacy-secret", plain);
        Assert.Null(error);
    }
}
