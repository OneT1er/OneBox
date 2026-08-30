using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using PowerAudioManager;
using Xunit;

namespace OneBox.Tests;

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
            "OneBox", "OneBox.perfhistory.json");

        Assert.Equal(expected, actual, ignoreCase: true);
    }
}

public sealed class HardwareMetricBindingTests
{
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
}
