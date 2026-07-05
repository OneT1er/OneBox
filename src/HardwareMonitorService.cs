using System;
using System.Collections.Generic;
using System.Linq;
using LibreHardwareMonitor.Hardware;

namespace PowerAudioManager
{
    public class SensorInfo
    {
        public string HardwareName { get; set; }
        public string SensorName { get; set; }
        public HardwareType HwType { get; set; }
        public override string ToString() => $"{HardwareName} — {SensorName}";
    }

    public class HardwareMonitorService : IDisposable
    {
        public static readonly HardwareMonitorService Instance = new HardwareMonitorService();

        private Computer _computer;
        private bool _started;
        private bool _hwReady;
        private readonly object _lock = new object();
        private int _updateCount;

        public float? CpuTemperature { get; private set; }
        public float? GpuTemperature { get; private set; }
        public bool IsAvailable { get; private set; }

        public List<SensorInfo> AvailableCpuSensors { get; } = new List<SensorInfo>();
        public List<SensorInfo> AvailableGpuSensors { get; } = new List<SensorInfo>();

        public string CpuSensorName { get; set; } = "";
        public string GpuSensorName { get; set; } = "";

        private HardwareMonitorService() { }

        public void Start()
        {
            if (_started) return;
            lock (_lock)
            {
                if (_started) return;
                _started = true;
            }

            System.Threading.ThreadPool.QueueUserWorkItem(_ =>
            {
                try
                {
                    _computer = new Computer
                    {
                        IsCpuEnabled = true,
                        IsGpuEnabled = true,
                        IsMotherboardEnabled = true,
                    };
                    _computer.Open();
                    _computer.Accept(new UpdateVisitor());
                    DiscoverSensors();
                    _hwReady = true;
                    IsAvailable = true;
                    // 首读
                    ReadCpu();
                    ReadGpu();
                    AppLog.Log("Temp", string.Format(
                        "ready: hw={0} cpuSensors={1} gpuSensors={2} admin={3} cpu={4}°C gpu={5}°C",
                        _computer.Hardware.Count,
                        AvailableCpuSensors.Count,
                        AvailableGpuSensors.Count,
                        AdminUtils.IsAdmin(),
                        CpuTemperature?.ToString("0") ?? "null",
                        GpuTemperature?.ToString("0") ?? "null"));
                }
                catch (Exception ex)
                {
                    AppLog.Log("Temp", "init failed: " + ex.Message);
                    _hwReady = false;
                }
            });
        }

        void DiscoverSensors()
        {
            AvailableCpuSensors.Clear();
            AvailableGpuSensors.Clear();

            void Scan(IHardware hw)
            {
                foreach (var s in hw.Sensors)
                {
                    if (s.SensorType != SensorType.Temperature) continue;
                    var info = new SensorInfo
                    {
                        HardwareName = hw.Name,
                        SensorName = s.Name,
                        HwType = hw.HardwareType
                    };

                    if (hw.HardwareType == HardwareType.Cpu ||
                        hw.HardwareType == HardwareType.Motherboard)
                    {
                        AvailableCpuSensors.Add(info);
                        AppLog.Log("Temp", $"  [CPU] {info}");
                    }

                    if (hw.HardwareType == HardwareType.GpuNvidia ||
                        hw.HardwareType == HardwareType.GpuAmd ||
                        hw.HardwareType == HardwareType.GpuIntel)
                    {
                        AvailableGpuSensors.Add(info);
                        AppLog.Log("Temp", $"  [GPU] {info}");
                    }
                }
                foreach (var sub in hw.SubHardware)
                    Scan(sub);
            }

            if (_computer != null)
                foreach (var hw in _computer.Hardware)
                    Scan(hw);
        }

        public void Update()
        {
            if (!IsAvailable || _computer == null || !_hwReady) return;
            try
            {
                _updateCount++;
                _computer.Accept(new UpdateVisitor());
                ReadCpu();
                ReadGpu();
            }
            catch { }
        }

        void ReadCpu()
        {
            try
            {
                SensorInfo target = null;
                if (!string.IsNullOrEmpty(CpuSensorName))
                    target = AvailableCpuSensors.FirstOrDefault(s => s.SensorName == CpuSensorName);

                float? val = null;

                void ScanHw(IHardware hw)
                {
                    if (hw.HardwareType != HardwareType.Cpu && hw.HardwareType != HardwareType.Motherboard) return;
                    foreach (var s in hw.Sensors)
                    {
                        if (s.SensorType != SensorType.Temperature) continue;
                        if (!IsValid(s.Value)) continue;

                        if (target != null && s.Name == target.SensorName)
                            { val = s.Value; return; }
                        if (target != null) continue;

                        if (val == null) val = s.Value;
                        if (s.Name.Contains("Package") || s.Name.Contains("Tctl") || s.Name.Contains("Tdie")
                            || s.Name.Contains("Die") || s.Name.Contains("Core Max"))
                            { val = s.Value; return; }
                    }
                }

                foreach (var hw in _computer.Hardware)
                {
                    ScanHw(hw);
                    foreach (var sub in hw.SubHardware)
                        try { ScanHw(sub); } catch { }
                }

                if (val.HasValue) CpuTemperature = val;
            }
            catch { }
        }

        void ReadGpu()
        {
            try
            {
                SensorInfo target = null;
                if (!string.IsNullOrEmpty(GpuSensorName))
                    target = AvailableGpuSensors.FirstOrDefault(s => s.SensorName == GpuSensorName);

                float? val = null;

                foreach (var hw in _computer.Hardware)
                {
                    if (hw.HardwareType != HardwareType.GpuNvidia &&
                        hw.HardwareType != HardwareType.GpuAmd &&
                        hw.HardwareType != HardwareType.GpuIntel)
                        continue;

                    foreach (var s in hw.Sensors)
                    {
                        if (s.SensorType != SensorType.Temperature) continue;
                        if (!IsValid(s.Value)) continue;

                        if (target != null && s.Name == target.SensorName)
                            { val = s.Value; break; }
                        if (target != null) continue;

                        if (val == null) val = s.Value;
                        if (s.Name.Contains("Core") || s.Name.Contains("GPU"))
                            { val = s.Value; break; }
                    }
                }

                if (val.HasValue) GpuTemperature = val;
            }
            catch { }
        }

        static bool IsValid(float? v) => v.HasValue && v.Value > 0 && v.Value < 150;

        public void Stop()
        {
            try { _computer?.Close(); } catch { }
            _computer = null;
            _started = false;
            _hwReady = false;
            _updateCount = 0;
            IsAvailable = false;
            AvailableCpuSensors.Clear();
            AvailableGpuSensors.Clear();
        }

        public void Dispose() => Stop();

        private class UpdateVisitor : IVisitor
        {
            public void VisitComputer(IComputer computer) { computer.Traverse(this); }
            public void VisitHardware(IHardware hardware)
            {
                hardware.Update();
                foreach (var sub in hardware.SubHardware) sub.Accept(this);
            }
            public void VisitSensor(ISensor sensor) { }
            public void VisitParameter(IParameter parameter) { }
        }
    }
}
