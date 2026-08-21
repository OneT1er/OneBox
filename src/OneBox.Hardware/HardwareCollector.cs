using System;
using System.Collections.Generic;
using System.Linq;
using LibreHardwareMonitor.Hardware;
using OneBox.Contracts;

namespace OneBox.Hardware;

internal sealed class HardwareCollector : IDisposable
{
    private readonly object _gate = new();
    private Computer _computer;

    public void Start()
    {
        lock (_gate)
        {
            _computer = new Computer
            {
                IsCpuEnabled = true,
                IsGpuEnabled = true,
                IsMotherboardEnabled = true,
                IsMemoryEnabled = true,
                IsStorageEnabled = true,
            };
            _computer.Open();
            _computer.Accept(new UpdateVisitor());
        }
    }

    public HardwareSnapshot ReadSnapshot()
    {
        lock (_gate) return ReadSnapshotCore();
    }

    private HardwareSnapshot ReadSnapshotCore()
    {
        if (_computer == null) return new HardwareSnapshot();
        _computer.Accept(new UpdateVisitor());
        var snapshot = new HardwareSnapshot { Ready = true };
        foreach (var hardware in Flatten(_computer.Hardware))
        {
            foreach (var sensor in hardware.Sensors)
            {
                if (sensor.SensorType != SensorType.Temperature && sensor.SensorType != SensorType.Fan && sensor.SensorType != SensorType.Control)
                    continue;
                if (sensor.SensorType == SensorType.Temperature && IsMetadataTemperature(sensor.Name))
                    continue;

                string sensorType = sensor.SensorType.ToString();
                string hardwareType = hardware.HardwareType.ToString();
                snapshot.Sensors.Add(new HardwareSensor
                {
                    HardwareName = hardware.Name,
                    SensorName = sensor.Name,
                    HardwareType = hardwareType,
                    SensorType = sensorType,
                });
                if (!sensor.Value.HasValue || !IsPlausible(sensor.SensorType, sensor.Value.Value))
                    continue;
                string displayName = DefaultDisplayName(hardware.Name, sensor.Name, sensorType);
                string icon = AutoIcon(displayName, hardware.Name, sensor.Name, sensorType);
                string unit = sensor.SensorType == SensorType.Temperature ? "°C" : sensor.SensorType == SensorType.Control ? "%" : "RPM";
                string key = $"{sensorType}|{hardware.Name}|{sensor.Name}|{displayName}|{icon}";
                snapshot.Metrics.Add(new HardwareMetric { Name = displayName, Icon = icon, Value = sensor.Value, Unit = unit, Key = key });
                if (sensor.SensorType == SensorType.Temperature)
                {
                    if (!snapshot.CpuTemperature.HasValue && hardware.HardwareType == HardwareType.Cpu)
                        snapshot.CpuTemperature = sensor.Value;
                    if (!snapshot.GpuTemperature.HasValue && IsGpu(hardware.HardwareType) && !sensor.Name.Contains("Hot", StringComparison.OrdinalIgnoreCase))
                        snapshot.GpuTemperature = sensor.Value;
                }
            }
        }
        return snapshot;
    }

    private static IEnumerable<IHardware> Flatten(IEnumerable<IHardware> source)
    {
        foreach (var hardware in source)
        {
            yield return hardware;
            foreach (var child in Flatten(hardware.SubHardware)) yield return child;
        }
    }

    private static bool IsGpu(HardwareType type) => type is HardwareType.GpuNvidia or HardwareType.GpuAmd or HardwareType.GpuIntel;

    private static bool IsPlausible(SensorType type, float value)
    {
        return type switch
        {
            SensorType.Temperature => value > 0 && value <= 150,
            SensorType.Control => value >= 0 && value <= 100,
            SensorType.Fan => value >= 0 && value <= 10000,
            _ => false,
        };
    }

    private static bool IsMetadataTemperature(string name)
    {
        string value = (name ?? string.Empty).ToLowerInvariant();
        return value.Contains("resolution") || value.Contains("limit") || value.Contains("warning") || value.Contains("critical");
    }

    private static string DefaultDisplayName(string hardwareName, string sensorName, string sensorType)
    {
        string hardware = (hardwareName ?? string.Empty).ToLowerInvariant();
        bool cpu = hardware.Contains("cpu") || hardware.Contains("ryzen") || hardware.Contains("intel");
        bool gpu = hardware.Contains("nvidia") || hardware.Contains("geforce") || hardware.Contains("rtx") || hardware.Contains("radeon");
        if (sensorType == nameof(SensorType.Temperature))
        {
            if (sensorName.Contains("Hot Spot", StringComparison.OrdinalIgnoreCase)) return "GPU HotSpot";
            if (sensorName.Contains("Memory", StringComparison.OrdinalIgnoreCase) || sensorName.Contains("Junction", StringComparison.OrdinalIgnoreCase)) return "VRAM";
            if (cpu) return "CPU";
            if (gpu) return "GPU";
            return "Temp";
        }
        if (sensorType == nameof(SensorType.Fan)) return cpu ? "CPU Fan" : gpu ? "GPU Fan" : sensorName;
        if (sensorType == nameof(SensorType.Control)) return cpu ? "CPU Fan%" : gpu ? "GPU Fan%" : sensorName;
        return sensorName;
    }

    private static string AutoIcon(string displayName, string hardwareName, string sensorName, string sensorType)
    {
        string display = (displayName ?? string.Empty).ToLowerInvariant();
        string hardware = (hardwareName ?? string.Empty).ToLowerInvariant();
        string sensor = (sensorName ?? string.Empty).ToLowerInvariant();
        if (sensorType == nameof(SensorType.Fan)) return "fan";
        if (sensorType == nameof(SensorType.Control)) return "ctrl";
        if (sensor.Contains("hot spot")) return "hot";
        if (sensor.Contains("memory") || sensor.Contains("junction")) return "vram";
        if (hardware.Contains("memory") || hardware.Contains("dram") || hardware.Contains("dimm")) return "dram";
        if (hardware.Contains("ssd") || hardware.Contains("hdd") || hardware.Contains("nvme") || hardware.Contains("disk")) return "disk";
        if (display.Contains("cpu")) return "cpu";
        if (display.Contains("gpu")) return "gpu";
        return "def";
    }

    public void Dispose()
    {
        lock (_gate)
        {
            try { _computer?.Close(); } catch { }
            _computer = null;
        }
    }

    private sealed class UpdateVisitor : IVisitor
    {
        public void VisitComputer(IComputer computer) => computer.Traverse(this);
        public void VisitHardware(IHardware hardware)
        {
            hardware.Update();
            foreach (var subHardware in hardware.SubHardware) subHardware.Accept(this);
        }
        public void VisitSensor(ISensor sensor) { }
        public void VisitParameter(IParameter parameter) { }
    }
}
