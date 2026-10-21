using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using LibreHardwareMonitor.Hardware;
using OneBox.Contracts;
using OneBox.Hardware;
using Xunit;
using LhmHardware = LibreHardwareMonitor.Hardware.Hardware;

namespace OneBox.Tests;

public sealed class SensorHistoryTests
{
    [Fact]
    public void RealSensorDisablingHistoryPreservesValueAndExtremaAndClearsExistingHistory()
    {
        var hardware = new OfflineHardware();
        ISensor normal = Create(hardware, SensorType.Temperature, 0);
        ISensor disabled = Create(hardware, SensorType.Temperature, 1);
        HardwareCollector.DisableSensorHistory(disabled);
        var valueProperty = normal.GetType().GetProperty("Value");
        for (int i = 0; i < 256; i++)
        {
            float? value = i % 7 == 0 ? null : i < 64 ? 50f : i % 101;
            valueProperty.SetValue(normal, value);
            valueProperty.SetValue(disabled, value);
            Assert.Equal(normal.Value, disabled.Value);
            Assert.Equal(normal.Min, disabled.Min);
            Assert.Equal(normal.Max, disabled.Max);
        }
        Assert.NotEmpty(normal.Values);
        Assert.Empty(disabled.Values);
        HardwareCollector.DisableSensorHistory(normal);
        Assert.Empty(normal.Values);
        float? min = normal.Min, max = normal.Max;
        for (int i = 0; i < 32; i++) valueProperty.SetValue(normal, 50f);
        Assert.Empty(normal.Values);
        Assert.Equal(min, normal.Min);
        Assert.Equal(max, normal.Max);
    }

    [Fact]
    public void CollectorDisablesHistoryForUnselectedAndNewSensorsBeforeFiltering()
    {
        var hardware = new OfflineHardware();
        ISensor load = Create(hardware, SensorType.Load, 0);
        hardware.ExposedSensors = new[] { load };
        var subscription = new HardwareSubscribePayload { SelectedSensors = new() };
        HardwareSnapshot snapshot = HardwareCollector.BuildSnapshot(new[] { hardware }, subscription, new HashSet<ISensor>());
        Assert.Empty(snapshot.Metrics);
        Assert.Equal(TimeSpan.Zero, load.ValuesTimeWindow);
        ISensor addedLater = Create(hardware, SensorType.Temperature, 1);
        hardware.ExposedSensors = new[] { load, addedLater };
        HardwareCollector.BuildSnapshot(new[] { hardware }, subscription, new HashSet<ISensor>());
        Assert.Equal(TimeSpan.Zero, addedLater.ValuesTimeWindow);
    }

    static ISensor Create(OfflineHardware hardware, SensorType kind, int index)
    {
        Type type = typeof(ISensor).Assembly.GetType("LibreHardwareMonitor.Hardware.Sensor", true);
        ConstructorInfo constructor = type.GetConstructor(new[] { typeof(string), typeof(int), typeof(SensorType), typeof(LhmHardware), typeof(ISettings) });
        return (ISensor)constructor.Invoke(new object[] { "Offline" + index, index, kind, hardware, new OfflineSettings() });
    }

    sealed class OfflineSettings : ISettings
    {
        public bool Contains(string name) => false;
        public string GetValue(string name, string fallback) => fallback;
        public void SetValue(string name, string value) { }
        public void Remove(string name) { }
    }

    sealed class OfflineHardware : LhmHardware, IHardware
    {
        public ISensor[] ExposedSensors = Array.Empty<ISensor>();
        ISensor[] IHardware.Sensors => ExposedSensors;
        public OfflineHardware() : base("Offline", new Identifier("offline"), new OfflineSettings()) { }
        public override HardwareType HardwareType => HardwareType.Motherboard;
        public override void Update() { }
    }
}
