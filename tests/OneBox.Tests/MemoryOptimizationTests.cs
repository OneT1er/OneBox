using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Text.Json;
using OneBox.Contracts;
using PowerAudioManager;
using Xunit;

namespace OneBox.Tests;

public sealed class MemoryListLayoutTests
{
    [Fact]
    public void FromPageCounts_SumsEveryStandbyPriorityAndModifiedList()
    {
        var fields = new ulong[MemoryListLayout.FieldCount];
        fields[MemoryListLayout.ModifiedPageIndex] = 10;
        fields[MemoryListLayout.FreePageIndex] = 999;      // not part of Cached
        fields[MemoryListLayout.ModifiedNoWritePageIndex] = 7;
        for (int i = 0; i < MemoryListLayout.PriorityCount; i++)
            fields[MemoryListLayout.StandbyByPriorityIndex + i] = (ulong)(i + 1); // 1..8 → 36 pages
        fields[MemoryListLayout.RepurposedByPriorityIndex] = 500; // counters, not resident pages

        MemoryListStatusResult result = MemoryListLayout.FromPageCounts(fields, 4096);

        Assert.Equal(36UL * 4096, result.StandbyBytes);
        Assert.Equal(10UL * 4096, result.ModifiedBytes);
        Assert.Equal(46UL * 4096, result.CachedBytes);
    }

    [Fact]
    public void FromPageCounts_RejectsTruncatedBuffers()
    {
        Assert.Throws<ArgumentException>(() => MemoryListLayout.FromPageCounts(new ulong[MemoryListLayout.FieldCount - 1], 4096));
        Assert.Throws<ArgumentOutOfRangeException>(() => MemoryListLayout.FromPageCounts(new ulong[MemoryListLayout.FieldCount], 0));
    }

    [Fact]
    public void MemoryListStatus_IsAcceptedOnTheCommandEndpointAndRoundTrips()
    {
        IpcRequest request = IpcRequest.Create(IpcCommand.MemoryListStatus, new { });
        Assert.True(IpcValidator.Validate(request, IpcCommand.CleanMemory, IpcCommand.Ping, IpcCommand.MemoryListStatus).IsValid);
        // Endpoints that were not updated keep rejecting it, so the GUI falls back.
        Assert.Equal(IpcErrorCode.UnsupportedCommand,
            IpcValidator.Validate(request, IpcCommand.CleanMemory, IpcCommand.Ping).ErrorCode);

        var sent = new MemoryListStatusResult { StandbyBytes = 3UL << 30, ModifiedBytes = 5UL << 20 };
        IpcResponse response = IpcResponse.Ok(request, sent);
        Assert.False(response.Result.TryGetProperty("cachedBytes", out _));
        Assert.False(response.Result.TryGetProperty("CachedBytes", out _));
        MemoryListStatusResult received = response.ReadResult<MemoryListStatusResult>();
        Assert.Equal(sent.StandbyBytes, received.StandbyBytes);
        Assert.Equal(sent.ModifiedBytes, received.ModifiedBytes);
        Assert.Equal(sent.CachedBytes, received.CachedBytes);
    }
}

public sealed class ForegroundNamePoolTests
{
    [Fact]
    public void PoolName_SharesOneInstancePerExecutable()
    {
        string a = ForegroundHistory.PoolName(new string("pool-test.exe".ToCharArray()));
        string b = ForegroundHistory.PoolName(new string("pool-test.exe".ToCharArray()));
        Assert.Same(a, b);
        Assert.Equal("", ForegroundHistory.PoolName(null));
        Assert.Equal("", ForegroundHistory.PoolName(""));
    }
}

[Collection("PerfHistory")]
public sealed class PerfHistoryChunkTests
{
    [Fact]
    public void NewSeries_AllocatesOnlyTheFirstChunk()
    {
        WithIsolatedHistory(null, series =>
        {
            var metrics = new List<MetricValue>
            {
                new MetricValue { ConfigKey = "chunk-check", DisplayName = "Chunk", IconKey = "cpu", Unit = "°C", Value = 42 },
            };
            for (int i = 0; i < 10; i++) PerfHistory.Add(metrics);

            object stored = series["chunk-check"];
            int chunks = (int)stored.GetType().GetProperty("AllocatedChunks").GetValue(stored);
            Assert.Equal(1, chunks);
            List<ChartSeries> chart = PerfHistory.GetSeries(DateTime.MinValue, DateTime.MaxValue);
            Assert.Equal(10, Assert.Single(chart).Points.Count);
        });
    }

    [Fact]
    public void StreamingLoad_HandlesBomUnknownPropertiesNumericTimesAndLargeFiles()
    {
        string directory = Path.Combine(Path.GetTempPath(), "OneBox.PerfStream." + Guid.NewGuid().ToString("N"));
        string path = Path.Combine(directory, "OneBox.perfhistory.json");
        Directory.CreateDirectory(directory);
        try
        {
            const int count = 20000; // ~300 KB: spans several 64 KB read windows
            long start = new DateTimeOffset(2026, 10, 1, 0, 0, 0, TimeSpan.Zero).ToUnixTimeSeconds();
            var json = new StringBuilder();
            json.Append("{\"big\":{\"extra\":{\"nested\":[1,2,{\"x\":3}]},\"name\":\"Big\",\"icon\":\"cpu\",\"isTemp\":true,\"points\":[");
            for (int i = 0; i < count; i++) { if (i > 0) json.Append(','); json.Append((i % 90).ToString(CultureInfo.InvariantCulture)); }
            json.Append("],\"times\":[");
            for (int i = 0; i < count; i++)
            {
                if (i > 0) json.Append(',');
                long t = start + i;
                // Mix the persisted string form with plain numbers.
                if (i % 2 == 0) json.Append('"').Append(t.ToString(CultureInfo.InvariantCulture)).Append('"');
                else json.Append(t.ToString(CultureInfo.InvariantCulture));
            }
            json.Append("]},\"fan\":{\"name\":\"Fan\",\"icon\":\"fan\",\"isTemp\":false,\"points\":[1200.5],\"times\":[\"")
                .Append(start.ToString(CultureInfo.InvariantCulture)).Append("\"]}}");
            File.WriteAllText(path, json.ToString(), new UTF8Encoding(encoderShouldEmitUTF8Identifier: true));

            WithIsolatedHistory(path, _ =>
            {
                Assert.True(PerfHistory.Load());
                Dictionary<string, ChartSeries> loaded = PerfHistory.GetSeries(DateTime.MinValue, DateTime.MaxValue)
                    .ToDictionary(item => item.Name);
                ChartSeries big = loaded["Big"];
                Assert.Equal(count, big.Points.Count);
                Assert.Equal((count - 1) % 90, big.Points[count - 1]);
                Assert.Equal(DateTimeOffset.FromUnixTimeSeconds(start).LocalDateTime, big.Times[0]);
                Assert.Equal(DateTimeOffset.FromUnixTimeSeconds(start + count - 1).LocalDateTime, big.Times[count - 1]);
                Assert.True(big.IsTemp);
                Assert.Equal(1200.5f, Assert.Single(loaded["Fan"].Points));
                Assert.False(loaded["Fan"].IsTemp);
            });
        }
        finally
        {
            try { Directory.Delete(directory, recursive: true); } catch { }
        }
    }

    [Fact]
    public void StreamingLoad_RejectsMalformedSeriesSoRecoveryCanQuarantineIt()
    {
        string directory = Path.Combine(Path.GetTempPath(), "OneBox.PerfStreamBad." + Guid.NewGuid().ToString("N"));
        string path = Path.Combine(directory, "OneBox.perfhistory.json");
        Directory.CreateDirectory(directory);
        try
        {
            File.WriteAllText(path, "{\"cpu\":{\"points\":[1,\"two\"]}}");
            WithIsolatedHistory(path, series =>
            {
                Assert.True(PerfHistory.Load());        // continues instead of failing startup
                // The corrupt file is moved aside as evidence, never parsed partially.
                Assert.Single(Directory.GetFiles(directory, "OneBox.perfhistory.json.corrupt-*"));
                Assert.False(series.Contains("cpu"));
            });
        }
        finally
        {
            try { Directory.Delete(directory, recursive: true); } catch { }
        }
    }

    // Runs body against an empty, isolated PerfHistory state and restores
    // the shared statics afterwards (same pattern as the existing save/load tests).
    private static void WithIsolatedHistory(string path, Action<IDictionary> body)
    {
        Type type = typeof(PerfHistory);
        FieldInfo pathField = Field(type, "_path");
        FieldInfo seriesField = Field(type, "_series");
        FieldInfo loadedField = Field(type, "_loaded");
        FieldInfo loadAttemptedField = Field(type, "_loadAttempted");
        object stateLock = Field(type, "_lock").GetValue(null);
        var series = (IDictionary)seriesField.GetValue(null);
        var previousSeries = new List<DictionaryEntry>();
        object previousPath;
        bool previousLoaded, previousLoadAttempted;
        lock (stateLock)
        {
            foreach (DictionaryEntry entry in series) previousSeries.Add(entry);
            previousPath = pathField.GetValue(null);
            previousLoaded = (bool)loadedField.GetValue(null);
            previousLoadAttempted = (bool)loadAttemptedField.GetValue(null);
            series.Clear();
            pathField.SetValue(null, path ?? Path.Combine(Path.GetTempPath(), "OneBox.unused." + Guid.NewGuid().ToString("N") + ".json"));
            // Mark as loaded so Add() never touches the real user history.
            loadedField.SetValue(null, path == null);
            loadAttemptedField.SetValue(null, true);
        }
        try { body(series); }
        finally
        {
            lock (stateLock)
            {
                series.Clear();
                foreach (DictionaryEntry entry in previousSeries) series.Add(entry.Key, entry.Value);
                pathField.SetValue(null, previousPath);
                loadedField.SetValue(null, previousLoaded);
                loadAttemptedField.SetValue(null, previousLoadAttempted);
            }
        }
    }

    private static FieldInfo Field(Type type, string name) =>
        type.GetField(name, BindingFlags.Static | BindingFlags.NonPublic)
        ?? throw new InvalidOperationException("PerfHistory field not found: " + name);
}
