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

        // WMI 是否成功提供了 CPU 温度（当 LHM 硬件扫描无 CPU 传感器时可用）
        public bool UsingWmiFallback { get; private set; }

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

            // 全部初始化在后台线程，不阻塞 UI
            System.Threading.ThreadPool.QueueUserWorkItem(_ =>
            {
                // 先试 WMI（快速，无需驱动）
                TryWmiCpuTemp();
                if (CpuTemperature.HasValue)
                {
                    IsAvailable = true;
                    UsingWmiFallback = true;
                    AppLog.Log("Temp", string.Format("WMI CPU={0:0}°C (fallback)", CpuTemperature.Value));
                }

                // 再开 LHM 硬件扫描
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

                    // WMI 已有值就不刷掉
                    if (!CpuTemperature.HasValue)
                        ReadCpuFromLhm();

                    AppLog.Log("Temp", string.Format(
                        "LHM ready: hw={0} cpuSensors={1} gpuSensors={2} admin={3} cpuTemp={4}",
                        _computer.Hardware.Count,
                        AvailableCpuSensors.Count,
                        AvailableGpuSensors.Count,
                        AdminUtils.IsAdmin(),
                        CpuTemperature?.ToString("0") ?? "null"));
                }
                catch (Exception ex)
                {
                    AppLog.Log("Temp", "LHM init failed: " + ex.Message);
                    _hwReady = false;
                    IsAvailable = CpuTemperature.HasValue;
                }
            });
        }

        void TryWmiCpuTemp()
        {
            try
            {
                using (var searcher = new System.Management.ManagementObjectSearcher(
                    @"root\WMI", "SELECT * FROM MSAcpi_ThermalZoneTemperature"))
                {
                    foreach (System.Management.ManagementObject obj in searcher.Get())
                    {
                        try
                        {
                            double raw = Convert.ToDouble(obj["CurrentTemperature"]);
                            double celsius = (raw / 10.0) - 273.15;
                            if (celsius > 0 && celsius < 150)
                            {
                                CpuTemperature = (float)celsius;
                                return;
                            }
                        }
                        catch { }
                    }
                }
            }
            catch (Exception ex)
            {
                AppLog.Log("Temp", "WMI query failed: " + ex.Message);
            }
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
            if (!IsAvailable) return;
            try
            {
                _updateCount++;

                // 强制 WMI 模式（用户选择了 "WMI (ACPI 热区)"）
                if (CpuSensorName == "__WMI__")
                {
                    TryWmiCpuTemp();
                    UsingWmiFallback = true;
                }
                else
                {
                    // WMI 兜底：每次更新都读 WMI（<1ms），LHM 读到 CPU 传感器时才覆盖
                    TryWmiCpuTemp();
                    if (CpuTemperature.HasValue)
                        UsingWmiFallback = AvailableCpuSensors.Count == 0;
                }

                // LHM 读取（如硬件已就绪）
                if (_hwReady && _computer != null)
                {
                    _computer.Accept(new UpdateVisitor());
                    if (CpuSensorName != "__WMI__")
                        ReadCpuFromLhm();
                    ReadGpuFromLhm();
                }

                if (_updateCount == 1)
                    AppLog.Log("Temp", string.Format(
                        "first update: CPU={0}°C GPU={1}°C wmi={2} lhmReady={3} cpuSensors={4}",
                        CpuTemperature?.ToString("0") ?? "null",
                        GpuTemperature?.ToString("0") ?? "null",
                        UsingWmiFallback, _hwReady, AvailableCpuSensors.Count));
            }
            catch (Exception ex)
            {
                if (_updateCount <= 3) AppLog.Log("Temp", "update err: " + ex.Message);
            }
        }

        void ReadCpuFromLhm()
        {
            if (_computer == null || !_hwReady) return;
            try
            {
                SensorInfo cpuTarget = null;
                if (!string.IsNullOrEmpty(CpuSensorName) && CpuSensorName != "Auto")
                    cpuTarget = AvailableCpuSensors.FirstOrDefault(s => s.SensorName == CpuSensorName);

                float? cpuTemp = null;

                void ScanHw(IHardware hw)
                {
                    if (hw.HardwareType != HardwareType.Cpu && hw.HardwareType != HardwareType.Motherboard) return;
                    foreach (var s in hw.Sensors)
                    {
                        if (s.SensorType != SensorType.Temperature) continue;
                        if (!IsValidTemp(s.Value)) continue;

                        if (cpuTarget != null && s.Name == cpuTarget.SensorName)
                            { cpuTemp = s.Value; return; }
                        if (cpuTarget != null) continue;

                        // 自动模式：优先封装级传感器
                        if (cpuTemp == null) cpuTemp = s.Value;
                        if (s.Name.Contains("Package") || s.Name.Contains("Tctl") || s.Name.Contains("Tdie")
                            || s.Name.Contains("Die") || s.Name.Contains("Core Max"))
                            { cpuTemp = s.Value; return; }
                    }
                }

                foreach (var hw in _computer.Hardware)
                {
                    ScanHw(hw);
                    foreach (var sub in hw.SubHardware)
                        try { ScanHw(sub); } catch { }
                }

                if (cpuTemp.HasValue)
                {
                    CpuTemperature = cpuTemp;
                    UsingWmiFallback = false;
                }
            }
            catch { }
        }

        void ReadGpuFromLhm()
        {
            try
            {
                SensorInfo gpuTarget = null;
                if (!string.IsNullOrEmpty(GpuSensorName) && GpuSensorName != "Auto")
                    gpuTarget = AvailableGpuSensors.FirstOrDefault(s => s.SensorName == GpuSensorName);

                float? gpuTemp = null;

                foreach (var hw in _computer.Hardware)
                {
                    if (hw.HardwareType != HardwareType.GpuNvidia &&
                        hw.HardwareType != HardwareType.GpuAmd &&
                        hw.HardwareType != HardwareType.GpuIntel)
                        continue;

                    foreach (var s in hw.Sensors)
                    {
                        if (s.SensorType != SensorType.Temperature) continue;
                        if (!IsValidTemp(s.Value)) continue;

                        if (gpuTarget != null && s.Name == gpuTarget.SensorName)
                            { gpuTemp = s.Value; break; }
                        if (gpuTarget != null) continue;

                        if (gpuTemp == null) gpuTemp = s.Value;
                        if (s.Name.Contains("Core") || s.Name.Contains("GPU"))
                            { gpuTemp = s.Value; break; }
                    }
                }

                GpuTemperature = gpuTemp;
            }
            catch { }
        }

        static bool IsValidTemp(float? v)
        {
            if (!v.HasValue) return false;
            return v.Value > 0 && v.Value < 150;
        }

        public void Stop()
        {
            try { _computer?.Close(); } catch { }
            _computer = null;
            _started = false;
            _hwReady = false;
            _updateCount = 0;
            IsAvailable = false;
            UsingWmiFallback = false;
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
