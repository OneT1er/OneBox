using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Text.Json;
using System.Threading;
using LibreHardwareMonitor.Hardware;
using OneBox.Contracts;
using OneBox.Hardware;
using PowerAudioManager;
using Xunit;

namespace OneBox.Tests;

public sealed class SelectedHardwareTests
{
    [Fact]
    public void ExplicitEmptyAndLegacyMissingSelectionsRemainDistinctOnWire()
    {
        var missing = JsonSerializer.Deserialize<HardwareSubscribePayload>("{}", IpcJson.Options);
        var empty = JsonSerializer.Deserialize<HardwareSubscribePayload>("{\"selectedSensors\":[]}", IpcJson.Options);
        Assert.Null(missing.SelectedSensors);
        Assert.Empty(empty.SelectedSensors);
        Assert.False(empty.IncludeCatalog);
        Assert.Empty(HardwareMonitorService.CreateSubscription(Array.Empty<string>(), false).SelectedSensors);
    }

    [Fact]
    public void SelectionUsesIdentityAndDeduplicatesDisplayVariants()
    {
        var payload = HardwareMonitorService.CreateSubscription(new[]
        {
            "Temperature|CPU|Package|CPU|cpu", "temperature|CPU|Package|Renamed|def", "invalid",
        }, false);
        HardwareSensorIdentity selected = Assert.Single(payload.SelectedSensors);
        Assert.Equal("CPU", selected.HardwareName);
        Assert.Equal("Package", selected.SensorName);
    }

    [Fact]
    public void DisabledMonitorLoadsExistingSelectionBeforeSettingsRenderAndDoesNotReloadEdits()
    {
        int reads = 0;
        string saved = string.Join(";", Enumerable.Range(0, 7).Select(i => $"Temperature|Board|Sensor{i}|Metric{i}|mb"));
        var service = new HardwareMonitorService(() => { reads++; return saved; });
        service.PrepareSelection();
        Assert.Equal(7, service.EnabledMetrics.Count);
        service.EnabledMetrics.Add("Temperature|Board|Added|New|mb");
        service.PrepareSelection();
        Assert.Equal(8, service.EnabledMetrics.Count);
        Assert.Equal(1, reads);
        Assert.Equal(8, service.CaptureSubscription(out _).SelectedSensors.Count);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    public void MissingOrEmptyPreferenceNeverCreatesDefaults(string saved)
    {
        var service = new HardwareMonitorService(() => saved);
        service.PrepareSelection();
        Assert.Empty(service.EnabledMetrics);
        Assert.Empty(service.CaptureSubscription(out _).SelectedSensors);
    }

    [Fact]
    public void ChangingSelectionCancelsPendingSubscriptionAndReconnectCapturesLatestSelection()
    {
        var service = new HardwareMonitorService(() => "Temperature|Board|Old|Old|mb");
        service.PrepareSelection();
        service.CaptureSubscription(out int oldRevision);
        using var pending = new CancellationTokenSource();
        Field("_subscriptionCancellation").SetValue(service, pending);
        service.ApplyEnabledMetrics(new() { "Temperature|Board|New|New|mb" });
        Assert.True(pending.IsCancellationRequested);
        HardwareSubscribePayload next = service.CaptureSubscription(out int revision);
        Assert.True(revision > oldRevision);
        Assert.Equal("New", Assert.Single(next.SelectedSensors).SensorName);
        Assert.False(service.ApplySnapshotIfCurrent(new HardwareSnapshot(), oldRevision));
        service.ApplyEnabledMetrics(new());
        Assert.Empty(service.CaptureSubscription(out _).SelectedSensors);
    }

    [Fact]
    public void DirectListEditsAreObservedByTheNextUpdate()
    {
        const string oldKey = "Temperature|Board|Old|Old|mb";
        const string newKey = "Temperature|Board|New|New|ram";
        var service = new HardwareMonitorService(() => oldKey);
        service.PrepareSelection();
        service.CaptureSubscription(out int revision);
        var snapshot = new HardwareSnapshot { Ready = true, SelectionApplied = true };
        snapshot.Metrics.Add(new HardwareMetric { Key = oldKey, Value = 40, Unit = "°C" });
        snapshot.Metrics.Add(new HardwareMetric { Key = newKey, Value = 55, Unit = "°C" });
        Assert.True(service.ApplySnapshotIfCurrent(snapshot, revision));
        service.EnabledMetrics[0] = newKey;
        service.Update();
        Assert.Equal(newKey, Assert.Single(service.ActiveMetrics).ConfigKey);
        Assert.Equal(55, Assert.Single(service.ActiveMetrics).Value);
        service.EnabledMetrics.Add(oldKey);
        service.Update();
        Assert.Equal(new[] { newKey, oldKey }, service.ActiveMetrics.Select(metric => metric.ConfigKey));
        service.EnabledMetrics[0] = "invalid";
        service.Update();
        Assert.Equal(oldKey, Assert.Single(service.ActiveMetrics).ConfigKey);
        object parsed = Field("_selectedMetrics").GetValue(service);
        service.Update();
        Assert.Same(parsed, Field("_selectedMetrics").GetValue(service));
        service.EnabledMetrics.Clear();
        service.Update();
        Assert.Empty(service.ActiveMetrics);
    }

    [Fact]
    public void SelectionReplacementWithSameCountUpdatesKeyLabelAndIcon()
    {
        const string oldKey = "Temperature|Board|Old|Old label|mb";
        const string newKey = "Temperature|Board|New|New label|ram";
        var service = new HardwareMonitorService(() => oldKey);
        service.PrepareSelection();
        service.CaptureSubscription(out int revision);
        var snapshot = new HardwareSnapshot { Ready = true, SelectionApplied = true };
        snapshot.Metrics.Add(new HardwareMetric { Key = oldKey, Value = 40, Unit = "°C" });
        snapshot.Metrics.Add(new HardwareMetric { Key = newKey, Value = 55, Unit = "°C" });
        Assert.True(service.ApplySnapshotIfCurrent(snapshot, revision));
        Assert.Equal(oldKey, Assert.Single(service.ActiveMetrics).ConfigKey);

        service.ApplyEnabledMetrics(new() { newKey });
        service.Update();
        MetricValue changed = Assert.Single(service.ActiveMetrics);
        Assert.Equal(newKey, changed.ConfigKey);
        Assert.Equal("New label", changed.DisplayName);
        Assert.Equal("ram", changed.IconKey);
        Assert.Equal(55, changed.Value);
        Assert.False(changed.Cached);
        service.ApplyEnabledMetrics(new());
        service.Update();
        Assert.Empty(service.ActiveMetrics);
    }

    [Fact]
    public void ParsedSelectionStillResolvesChangedSnapshotIdentityAndRejectsAmbiguity()
    {
        const string key = "Temperature|Old board|DIMM|Memory|ram";
        var service = new HardwareMonitorService(() => key);
        service.PrepareSelection();
        service.CaptureSubscription(out int revision);
        HardwareSnapshot Frame(string board, float value)
        {
            var frame = new HardwareSnapshot { Ready = true, SelectionApplied = true };
            frame.Sensors.Add(new HardwareSensor { HardwareName = board, SensorName = "DIMM", SensorType = "Temperature" });
            frame.Metrics.Add(new HardwareMetric { Key = $"Temperature|{board}|DIMM|Memory|ram", Value = value, Unit = "°C" });
            return frame;
        }
        Assert.True(service.ApplySnapshotIfCurrent(Frame("Board A", 41), revision));
        Assert.Equal(41, Assert.Single(service.ActiveMetrics).Value);
        Assert.True(service.ApplySnapshotIfCurrent(Frame("Board B", 63), revision));
        Assert.Equal(63, Assert.Single(service.ActiveMetrics).Value);
        Assert.False(Assert.Single(service.ActiveMetrics).Cached);

        HardwareSnapshot ambiguous = Frame("Board B", 72);
        ambiguous.Sensors.Add(new HardwareSensor { HardwareName = "Board C", SensorName = "DIMM", SensorType = "Temperature" });
        ambiguous.Metrics.Add(new HardwareMetric { Key = "Temperature|Board C|DIMM|Memory|ram", Value = 73, Unit = "°C" });
        Assert.True(service.ApplySnapshotIfCurrent(ambiguous, revision));
        Assert.True(Assert.Single(service.ActiveMetrics).Cached);
        Assert.Equal(63, Assert.Single(service.ActiveMetrics).Value);
    }

    [Fact]
    public void CollectorEmitsOnlySelectedDetailsButKeepsCpuGpuSummaries()
    {
        IHardware[] hardware = CreateHardware();
        var payload = new HardwareSubscribePayload { SelectedSensors = Enumerable.Range(0, 7).Select(i => Identity("Board", "Sensor" + i)).ToList() };
        var selected = HardwareCollector.ResolveSelectedSensorsCore(hardware, payload.SelectedSensors);
        HardwareSnapshot snapshot = HardwareCollector.BuildSnapshot(hardware, payload, selected);
        Assert.True(snapshot.SelectionApplied);
        Assert.False(snapshot.CatalogIncluded);
        Assert.Equal(7, snapshot.Metrics.Count);
        Assert.Equal(7, snapshot.Sensors.Count);
        Assert.Equal(50f, snapshot.CpuTemperature);
        Assert.Equal(40f, snapshot.GpuTemperature);
        Assert.DoesNotContain(snapshot.Sensors, sensor => sensor.SensorName == "Unused");

        payload.SelectedSensors.Clear();
        snapshot = HardwareCollector.BuildSnapshot(hardware, payload, HardwareCollector.ResolveSelectedSensorsCore(hardware, payload.SelectedSensors));
        Assert.Empty(snapshot.Metrics);
        Assert.Empty(snapshot.Sensors);
        Assert.Equal(50f, snapshot.CpuTemperature);
        Assert.Equal(40f, snapshot.GpuTemperature);

        payload.IncludeCatalog = true;
        snapshot = HardwareCollector.BuildSnapshot(hardware, payload, null);
        Assert.True(snapshot.CatalogIncluded);
        Assert.Equal(10, snapshot.Sensors.Count);
        Assert.Equal(10, snapshot.Metrics.Count);
        snapshot = HardwareCollector.BuildSnapshot(hardware, new HardwareSubscribePayload(), null);
        Assert.True(snapshot.CatalogIncluded);
        Assert.False(snapshot.SelectionApplied);
        Assert.Equal(10, snapshot.Sensors.Count);
    }

    [Fact]
    public void CollectorUniqueDriftFallbackRejectsAmbiguityAndUnknownIdentity()
    {
        IHardware[] hardware = CreateHardware();
        var selection = new List<HardwareSensorIdentity> { Identity("Old board", "Sensor0"), Identity("Board", "Sensor0"), Identity("Unknown", "Missing") };
        Assert.Single(HardwareCollector.ResolveSelectedSensorsCore(hardware, selection));
        IHardware[] ambiguous = hardware.Concat(new[] { Hardware("Other", HardwareType.Motherboard, Sensor("Sensor0", 30)) }).ToArray();
        Assert.Empty(HardwareCollector.ResolveSelectedSensorsCore(ambiguous, new() { Identity("Old board", "Sensor0") }));
        Assert.Single(HardwareCollector.ResolveSelectedSensorsCore(ambiguous, new() { Identity("Board", "Sensor0") }));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void CatalogLeaseReleasesOnlyItsOwnMonitorOwnershipAndRejectsLateFrames(bool monitoring)
    {
        var service = new HardwareMonitorService(() => "");
        service.PrepareSelection();
        // Keep the real lifecycle methods offline; the test never opens a service pipe.
        Field("_started").SetValue(service, true);
        if (monitoring) service.Start();
        service.EnabledMetrics.Add("Temperature|Board|Sensor0|Board|mb");
        using (service.BeginCatalogPreview())
        {
            HardwareSubscribePayload catalog = service.CaptureSubscription(out int catalogRevision);
            Assert.True(catalog.IncludeCatalog);
            var snapshot = new HardwareSnapshot { Ready = true, CatalogIncluded = true, SelectionApplied = true };
            snapshot.Sensors.Add(new HardwareSensor { HardwareName = "Board", SensorName = "Sensor0", SensorType = "Temperature" });
            Assert.True(service.ApplySnapshotIfCurrent(snapshot, catalogRevision));
            Assert.True(service.CatalogAvailable);
        }
        HardwareSubscribePayload normal = service.CaptureSubscription(out int revision);
        Assert.False(normal.IncludeCatalog);
        Assert.False(service.CatalogAvailable);
        Assert.Empty(service.AllTempSensors);
        Assert.Equal(monitoring, (bool)Field("_started").GetValue(service));
        Assert.False(service.ApplySnapshotIfCurrent(new HardwareSnapshot { CatalogIncluded = true }, revision - 1));
        service.EnabledMetrics.Clear();
        Assert.Empty(service.CaptureSubscription(out _).SelectedSensors);
        service.Stop();
    }

    private static FieldInfo Field(string name) => typeof(HardwareMonitorService).GetField(name, BindingFlags.Instance | BindingFlags.NonPublic);
    private static HardwareSensorIdentity Identity(string hardware, string sensor) => new() { HardwareName = hardware, SensorName = sensor, SensorType = "Temperature" };
    private static IHardware[] CreateHardware() => new[]
    {
        Hardware("CPU", HardwareType.Cpu, Sensor("Package", 50)),
        Hardware("GPU", HardwareType.GpuNvidia, Sensor("Core", 40)),
        Hardware("Board", HardwareType.Motherboard, Enumerable.Range(0, 7).Select(i => Sensor("Sensor" + i, 30 + i)).Append(Sensor("Unused", 42)).ToArray()),
    };

    private static IHardware Hardware(string name, HardwareType type, params ISensor[] sensors) => Stub<IHardware>(new()
    {
        ["Name"] = name, ["HardwareType"] = type, ["Sensors"] = sensors, ["SubHardware"] = Array.Empty<IHardware>(),
    });
    private static ISensor Sensor(string name, float value) => Stub<ISensor>(new()
    {
        ["Name"] = name, ["SensorType"] = SensorType.Temperature, ["Value"] = (float?)value,
        ["ValuesTimeWindow"] = TimeSpan.Zero,
    });
    private static T Stub<T>(Dictionary<string, object> properties) where T : class
    {
        T proxy = DispatchProxy.Create<T, PropertyStub>();
        ((PropertyStub)(object)proxy).Properties = properties;
        return proxy;
    }
    public class PropertyStub : DispatchProxy
    {
        public Dictionary<string, object> Properties;
        protected override object Invoke(MethodInfo method, object[] args) => Properties[method.Name[4..]];
    }
}
