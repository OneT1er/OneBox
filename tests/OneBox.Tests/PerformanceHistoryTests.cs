using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Security.Cryptography;
using System.Text.Json;
using PowerAudioManager;
using Xunit;

namespace OneBox.Tests;

[Collection("PerfHistory")]
public sealed class DurableFileStoreTests
{
    [Fact]
    public void AtomicWrite_RotatesThePreviousCommittedGeneration()
    {
        string directory = Path.Combine(Path.GetTempPath(), "OneBox.Tests." + Guid.NewGuid().ToString("N"));
        string path = Path.Combine(directory, "history.json");
        try
        {
            DurableFileStore.WriteUtf8Atomically(path, "first");
            DurableFileStore.WriteUtf8Atomically(path, "second");

            Assert.Equal("second", File.ReadAllText(path));
            Assert.Equal("first", File.ReadAllText(path + ".bak"));
            Assert.Empty(Directory.GetFiles(directory, "*.tmp.*"));
        }
        finally
        {
            try { if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true); } catch { }
        }
    }

    [Fact]
    public void RecoveryWrite_ReplacesCorruptPrimaryWithoutDestroyingGoodBackup()
    {
        string directory = Path.Combine(Path.GetTempPath(), "OneBox.Tests." + Guid.NewGuid().ToString("N"));
        string path = Path.Combine(directory, "history.json");
        try
        {
            Directory.CreateDirectory(directory);
            File.WriteAllText(path, "corrupt");
            File.WriteAllText(path + ".bak", "last-known-good");

            DurableFileStore.WriteUtf8Atomically(path, "restored", preserveBackup: true);

            Assert.Equal("restored", File.ReadAllText(path));
            Assert.Equal("last-known-good", File.ReadAllText(path + ".bak"));
        }
        finally
        {
            try { if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true); } catch { }
        }
    }

    [Fact]
    public void PerfHistory_UsesStableLocalAppDataInsteadOfVersionDirectory()
    {
        PropertyInfo property = typeof(PerfHistory).GetProperty("FilePath", BindingFlags.Static | BindingFlags.NonPublic);
        Assert.NotNull(property);

        string actual = Assert.IsType<string>(property.GetValue(null));
        string expected = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "OneT1er", "OneBox", "OneBox.perfhistory.json");

        Assert.Equal(expected, actual, ignoreCase: true);
    }
}

[Collection("PerfHistory")]
public sealed class PerfHistorySaveTests
{
    private readonly ITestOutputHelper _output;

    public PerfHistorySaveTests(ITestOutputHelper output) => _output = output;

    [Fact]
    public void Save_FullCapacityRoundTripsAndStaysWithinAllocationBudget()
    {
        string directory = Path.Combine(Path.GetTempPath(), "OneBox.PerfHistory." + Guid.NewGuid().ToString("N"));
        string path = Path.Combine(directory, "OneBox.perfhistory.json");
        Type type = typeof(PerfHistory);
        FieldInfo pathField = Field(type, "_path");
        FieldInfo seriesField = Field(type, "_series");
        FieldInfo loadedField = Field(type, "_loaded");
        FieldInfo loadAttemptedField = Field(type, "_loadAttempted");
        FieldInfo preserveBackupField = Field(type, "_preserveBackupOnNextSave");
        object saveIoLock = Field(type, "_saveIoLock").GetValue(null);
        object stateLock = Field(type, "_lock").GetValue(null);

        lock (saveIoLock)
        {
            var series = (IDictionary)seriesField.GetValue(null);
            var previousSeries = new List<DictionaryEntry>();
            lock (stateLock)
            {
                foreach (DictionaryEntry entry in series) previousSeries.Add(entry);
            }
            object previousPath = pathField.GetValue(null);
            bool previousLoaded = (bool)loadedField.GetValue(null);
            bool previousLoadAttempted = (bool)loadAttemptedField.GetValue(null);
            bool previousPreserveBackup = (bool)preserveBackupField.GetValue(null);

            try
            {
                lock (stateLock)
                {
                    series.Clear();
                    pathField.SetValue(null, path);
                    loadedField.SetValue(null, true);
                    loadAttemptedField.SetValue(null, true);
                    preserveBackupField.SetValue(null, false);
                }

                const int seriesCount = 8;
                var metrics = new List<MetricValue>(seriesCount);
                for (int i = 0; i < seriesCount; i++)
                {
                    metrics.Add(new MetricValue
                    {
                        ConfigKey = "save-check-" + i,
                        DisplayName = "Save check " + i,
                        IconKey = "Temp",
                        Unit = "°C",
                        Value = 0,
                    });
                }

                for (int sample = 0; sample < PerfHistory.Capacity; sample++)
                {
                    float value = sample % 100;
                    foreach (MetricValue metric in metrics) metric.Value = value;
                    PerfHistory.Add(metrics);
                }

                PerfHistory.Save(); // Warm JIT and writer buffers before measuring the full-capacity save.
                byte[] expectedHash;
                using (var stream = File.OpenRead(path)) expectedHash = SHA256.HashData(stream);
                GC.Collect();
                GC.WaitForPendingFinalizers();
                GC.Collect();
                long allocatedBefore = GC.GetAllocatedBytesForCurrentThread();
                var saveStopwatch = Stopwatch.StartNew();
                PerfHistory.Save();
                saveStopwatch.Stop();
                long allocatedBytes = GC.GetAllocatedBytesForCurrentThread() - allocatedBefore;
                _output.WriteLine("Full-capacity save allocated {0:N1} MiB in {1:N0} ms.",
                    allocatedBytes / 1024d / 1024d, saveStopwatch.Elapsed.TotalMilliseconds);
                Assert.True(allocatedBytes < 16L * 1024 * 1024,
                    $"Expected a full save to allocate under 16 MiB; allocated {allocatedBytes / 1024d / 1024d:N1} MiB.");
                using (var stream = File.OpenRead(path)) Assert.Equal(expectedHash, SHA256.HashData(stream));

                using (var stream = File.OpenRead(path))
                using (JsonDocument document = JsonDocument.Parse(stream))
                {
                    JsonElement root = document.RootElement;
                    Assert.Equal(seriesCount, root.EnumerateObject().Count());
                    foreach (MetricValue metric in metrics)
                    {
                        JsonElement saved = root.GetProperty(metric.ConfigKey);
                        Assert.Equal(metric.DisplayName, saved.GetProperty("name").GetString());
                        JsonElement points = saved.GetProperty("points");
                        Assert.Equal(PerfHistory.Capacity, points.GetArrayLength());
                        Assert.Equal(0, points[0].GetSingle());
                        Assert.Equal((PerfHistory.Capacity - 1) % 100, points[PerfHistory.Capacity - 1].GetSingle());
                        JsonElement times = saved.GetProperty("times");
                        Assert.Equal(PerfHistory.Capacity, times.GetArrayLength());
                        Assert.Equal(JsonValueKind.String, times[0].ValueKind);
                        Assert.True(long.TryParse(times[0].GetString(), NumberStyles.Integer,
                            CultureInfo.InvariantCulture, out _));
                    }
                }

                lock (stateLock) series.Clear();
                Assert.True(PerfHistory.Load());
                List<ChartSeries> loaded = PerfHistory.GetSeries(DateTime.MinValue, DateTime.MaxValue);
                Assert.Equal(seriesCount, loaded.Count);
                Assert.All(loaded, item =>
                {
                    Assert.Equal(PerfHistory.Capacity, item.Points.Count);
                    Assert.Equal(PerfHistory.Capacity, item.Times.Count);
                    Assert.Equal(0, item.Points[0]);
                    Assert.Equal((PerfHistory.Capacity - 1) % 100, item.Points[item.Points.Count - 1]);
                });
                Assert.Empty(Directory.GetFiles(directory, "*.tmp.*"));
            }
            finally
            {
                lock (stateLock)
                {
                    series.Clear();
                    foreach (DictionaryEntry entry in previousSeries) series.Add(entry.Key, entry.Value);
                    pathField.SetValue(null, previousPath);
                    loadedField.SetValue(null, previousLoaded);
                    loadAttemptedField.SetValue(null, previousLoadAttempted);
                    preserveBackupField.SetValue(null, previousPreserveBackup);
                }
                try { if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true); } catch { }
            }
        }
    }

    private static FieldInfo Field(Type type, string name) =>
        type.GetField(name, BindingFlags.Static | BindingFlags.NonPublic)
        ?? throw new InvalidOperationException("PerfHistory field not found: " + name);
}

[Collection("PerfHistory")]
public sealed class PerfHistoryLoadTests
{
    [Fact]
    public void Load_ReadsUnixIsoAndLegacyMissingTimestampsFromPrimaryFile()
    {
        string directory = Path.Combine(Path.GetTempPath(), "OneBox.PerfHistoryLoad." + Guid.NewGuid().ToString("N"));
        string path = Path.Combine(directory, "OneBox.perfhistory.json");
        Type type = typeof(PerfHistory);
        FieldInfo pathField = Field(type, "_path");
        FieldInfo seriesField = Field(type, "_series");
        object stateLock = Field(type, "_lock").GetValue(null);
        var series = (IDictionary)seriesField.GetValue(null);
        var previousSeries = new List<DictionaryEntry>();
        lock (stateLock)
        {
            foreach (DictionaryEntry entry in series) previousSeries.Add(entry);
        }
        object previousPath = pathField.GetValue(null);

        try
        {
            Directory.CreateDirectory(directory);
            lock (stateLock)
            {
                series.Clear();
                pathField.SetValue(null, path);
            }

            DateTimeOffset unixStart = new(2026, 10, 19, 12, 0, 0, TimeSpan.Zero);
            DateTimeOffset isoStart = new(2026, 10, 19, 13, 0, 0, TimeSpan.Zero);
            var data = new Dictionary<string, object>
            {
                ["unix"] = new
                {
                    name = "Unix",
                    icon = "def",
                    isTemp = true,
                    points = new[] { 1f, 2f },
                    times = new[]
                    {
                        unixStart.ToUnixTimeSeconds().ToString(CultureInfo.InvariantCulture),
                        unixStart.AddSeconds(1).ToUnixTimeSeconds().ToString(CultureInfo.InvariantCulture),
                    },
                },
                ["iso"] = new
                {
                    name = "ISO",
                    icon = "def",
                    isTemp = true,
                    points = new[] { 3f, 4f },
                    times = new[]
                    {
                        isoStart.UtcDateTime.ToString("O", CultureInfo.InvariantCulture),
                        isoStart.AddSeconds(1).UtcDateTime.ToString("O", CultureInfo.InvariantCulture),
                    },
                },
                ["legacy"] = new
                {
                    name = "Legacy",
                    icon = "def",
                    isTemp = true,
                    points = new[] { 5f, 6f },
                },
            };
            File.WriteAllText(path, JsonSerializer.Serialize(data));
            File.SetLastWriteTime(path, new DateTime(2026, 10, 19, 14, 0, 0, DateTimeKind.Local));
            DateTime fileTime = File.GetLastWriteTime(path);

            Assert.True(PerfHistory.Load());

            Dictionary<string, ChartSeries> loaded = PerfHistory.GetSeries(DateTime.MinValue, DateTime.MaxValue)
                .ToDictionary(item => item.Name);
            Assert.Equal(unixStart.LocalDateTime, loaded["Unix"].Times[0]);
            Assert.Equal(unixStart.AddSeconds(1).LocalDateTime, loaded["Unix"].Times[1]);
            Assert.Equal(isoStart.LocalDateTime, loaded["ISO"].Times[0]);
            Assert.Equal(isoStart.AddSeconds(1).LocalDateTime, loaded["ISO"].Times[1]);
            Assert.Equal(fileTime.AddSeconds(-1), loaded["Legacy"].Times[0]);
            Assert.Equal(fileTime, loaded["Legacy"].Times[1]);
        }
        finally
        {
            lock (stateLock)
            {
                series.Clear();
                foreach (DictionaryEntry entry in previousSeries) series.Add(entry.Key, entry.Value);
                pathField.SetValue(null, previousPath);
            }
            try { if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true); } catch { }
        }
    }

    private static FieldInfo Field(Type type, string name) =>
        type.GetField(name, BindingFlags.Static | BindingFlags.NonPublic)
        ?? throw new InvalidOperationException("PerfHistory field not found: " + name);
}

public sealed class HardwareMetricBindingTests
{
    [Fact]
    public void SnapshotFallback_PreservesSplitSegmentAndComparisonSemantics()
    {
        var configured = Sensor("", "DIMM #3");
        configured.SensorType = "temperature";
        var metric = Metric("Temperature||DIMM #3|extra");

        MetricValue found = HardwareMonitorService.FindUniqueSnapshotMetric(
            new List<MetricValue> { metric }, configured);

        Assert.Same(metric, found);
    }

    [Fact]
    public void SnapshotFallback_RequiresOrdinalHardwareAndSensorNames()
    {
        var configured = Sensor("Hardware", "Sensor");

        Assert.Null(HardwareMonitorService.FindUniqueSnapshotMetric(
            new List<MetricValue> { Metric("Temperature|hardware|Sensor") }, configured));
        Assert.Null(HardwareMonitorService.FindUniqueSnapshotMetric(
            new List<MetricValue> { Metric("Temperature|Hardware|sensor") }, configured));
        Assert.Null(HardwareMonitorService.FindUniqueSnapshotMetric(
            new List<MetricValue> { Metric("Temperature|Hardware") }, configured));
    }

    [Fact]
    public void SnapshotFallback_ReturnsNullWhenMoreThanOneMetricMatches()
    {
        var configured = Sensor("Hardware", "Sensor");
        var first = Metric("Temperature|Hardware|Sensor");
        var second = Metric("Temperature|Hardware|Sensor|extra");

        Assert.Null(HardwareMonitorService.FindUniqueSnapshotMetric(
            new List<MetricValue> { first, second }, configured));
    }

    [Fact]
    public void ProcessPath_ReturnsTheCurrentExecutableAndEmptyForMissingProcess()
    {
        Assert.Equal(Environment.ProcessPath,
            PowerAudioManager.AudioStudio.StudioDevices.ProcessPath(Environment.ProcessId), StringComparer.OrdinalIgnoreCase);
        Assert.Equal("", PowerAudioManager.AudioStudio.StudioDevices.ProcessPath(-1));
    }

    [Fact]
    public void DimmSensor_RebindsWhenOnlySpdHardwareNameDrifts()
    {
        var configured = Sensor("old or garbled SPD model (#3)", "DIMM #3");
        var actual = Sensor("A-DATA Technology - AX5U6000C2816G-BB300G (#3)", "DIMM #3");

        SensorInfo resolved = HardwareMonitorService.ResolveSensor(new[] { actual }, configured);

        Assert.Same(actual, resolved);
    }

    [Fact]
    public void Fallback_RejectsAmbiguousSameNamedSensors()
    {
        var configured = Sensor("old disk", "Temperature #1");
        var first = Sensor("Disk A", "Temperature #1");
        var second = Sensor("Disk B", "Temperature #1");

        SensorInfo resolved = HardwareMonitorService.ResolveSensor(new[] { first, second }, configured);

        Assert.Null(resolved);
    }

    [Fact]
    public void MissingDimmValue_ProducesVisibleCachedPlaceholder()
    {
        var configured = Sensor("A-DATA (#3)", "DIMM #3");

        MetricValue metric = HardwareMonitorService.CreateUnavailableMetric(
            configured, "saved-key", "Temp3", "dram");

        Assert.Equal("Temp3", metric.DisplayName);
        Assert.Equal("°C", metric.Unit);
        Assert.Equal("saved-key", metric.ConfigKey);
        Assert.Null(metric.Value);
        Assert.True(metric.Cached);
    }

    private static SensorInfo Sensor(string hardware, string name) => new()
    {
        HardwareName = hardware,
        SensorName = name,
        HwType = "Memory",
        SensorType = "Temperature",
    };

    private static MetricValue Metric(string configKey) => new() { ConfigKey = configKey };
}
