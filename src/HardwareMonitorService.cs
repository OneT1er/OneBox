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
        public SensorType SensorType { get; set; }
        public override string ToString() => $"{HardwareName} — {SensorName}";
    }

    public class MetricValue
    {
        public string Label;     // "CPU", "GPU", "Hot Spot", etc.
        public string Icon;      // "🌡", "🎮", "🔥", etc.
        public float? Value;
        public string Unit;      // "°C", "RPM", "%"
        public bool IsTemp => Unit == "°C";
    }

    public class HardwareMonitorService : IDisposable
    {
        public static readonly HardwareMonitorService Instance = new HardwareMonitorService();

        private Computer _computer;
        private bool _started, _hwReady;
        private readonly object _lock = new object();

        public bool IsAvailable { get; private set; }
        public List<SensorInfo> AvailableCpuSensors { get; } = new();
        public List<SensorInfo> AvailableGpuSensors { get; } = new();
        public List<SensorInfo> AvailableFanSensors { get; } = new();

        // ---- 用户可选指标（注册表持久化）----
        // 每个指标对应一个 sensor 类别，可独立开关
        public List<MetricValue> ActiveMetrics { get; } = new();  // 轮询后填充，UI 直接用

        public string CpuSensorName { get; set; } = "";
        public string GpuSensorName { get; set; } = "";

        private HardwareMonitorService() { }

        public void Start()
        {
            if (_started) return;
            lock (_lock) { if (_started) return; _started = true; }

            System.Threading.ThreadPool.QueueUserWorkItem(_ =>
            {
                try
                {
                    _computer = new Computer { IsCpuEnabled = true, IsGpuEnabled = true, IsMotherboardEnabled = true };
                    _computer.Open();
                    _computer.Accept(new UpdateVisitor());
                    DiscoverSensors();
                    _hwReady = true;
                    IsAvailable = true;
                    AppLog.Log("Temp", string.Format(
                        "ready: hw={0} cpu={1} gpu={2} fan={3} admin={4}",
                        _computer.Hardware.Count,
                        AvailableCpuSensors.Count, AvailableGpuSensors.Count, AvailableFanSensors.Count,
                        AdminUtils.IsAdmin()));
                }
                catch (Exception ex) { AppLog.Log("Temp", "init fail: " + ex.Message); _hwReady = false; }
            });
        }

        void DiscoverSensors()
        {
            AvailableCpuSensors.Clear(); AvailableGpuSensors.Clear(); AvailableFanSensors.Clear();

            void Scan(IHardware hw)
            {
                foreach (var s in hw.Sensors)
                {
                    var info = new SensorInfo { HardwareName = hw.Name, SensorName = s.Name, HwType = hw.HardwareType, SensorType = s.SensorType };

                    if (s.SensorType == SensorType.Temperature)
                    {
                        if (hw.HardwareType == HardwareType.Cpu || hw.HardwareType == HardwareType.Motherboard)
                        { AvailableCpuSensors.Add(info); AppLog.Log("Temp", $"  [CPU] {info}"); }
                        else if (hw.HardwareType == HardwareType.GpuNvidia || hw.HardwareType == HardwareType.GpuAmd || hw.HardwareType == HardwareType.GpuIntel)
                        { AvailableGpuSensors.Add(info); AppLog.Log("Temp", $"  [GPU] {info}"); }
                    }
                    if (s.SensorType == SensorType.Fan || s.SensorType == SensorType.Control)
                    { AvailableFanSensors.Add(info); AppLog.Log("Temp", $"  [FAN] {info}"); }
                }
                foreach (var sub in hw.SubHardware) Scan(sub);
            }
            if (_computer != null) foreach (var hw in _computer.Hardware) Scan(hw);
        }

        public void Update()
        {
            if (!IsAvailable || _computer == null || !_hwReady) return;
            try
            {
                _computer.Accept(new UpdateVisitor());
                var list = new List<MetricValue>();

                // --- CPU 温度 ---
                if (AppPrefs.GetBool("Monitor.ShowCpuTemp", true))
                {
                    var v = ReadCpuTemp();
                    if (v.HasValue) list.Add(new MetricValue { Label = "CPU", Icon = "\U0001F321", Value = v, Unit = "°C" });
                }

                // --- GPU 温度 ---
                if (AppPrefs.GetBool("Monitor.ShowGpuTemp", true))
                    foreach (var g in ReadGpuTemps())
                        list.Add(g);

                // --- 风扇 ---
                if (AppPrefs.GetBool("Monitor.ShowCpuFan", false))
                {
                    var v = ReadFan("Cpu");
                    if (v.HasValue) list.Add(new MetricValue { Label = "CPU Fan", Icon = "\U0001F300", Value = v, Unit = "RPM" });
                }
                if (AppPrefs.GetBool("Monitor.ShowGpuFan", false))
                {
                    var v = ReadFan("Gpu");
                    if (v.HasValue) list.Add(new MetricValue { Label = "GPU Fan", Icon = "\U0001F32A", Value = v, Unit = "RPM" });
                }

                lock (_lock) { ActiveMetrics.Clear(); ActiveMetrics.AddRange(list); }
            }
            catch { }
        }

        float? ReadCpuTemp()
        {
            SensorInfo target = null;
            if (!string.IsNullOrEmpty(CpuSensorName)) target = AvailableCpuSensors.FirstOrDefault(s => s.SensorName == CpuSensorName);

            float? val = null;
            void Scan(IHardware hw)
            {
                if (hw.HardwareType != HardwareType.Cpu && hw.HardwareType != HardwareType.Motherboard) return;
                foreach (var s in hw.Sensors)
                {
                    if (s.SensorType != SensorType.Temperature) continue;
                    if (!Valid(s.Value)) continue;
                    if (target != null && s.Name == target.SensorName) { val = s.Value; return; }
                    if (target != null) continue;
                    if (val == null) val = s.Value;
                    if (s.Name.Contains("Package") || s.Name.Contains("Tctl") || s.Name.Contains("Tdie") || s.Name.Contains("Die") || s.Name.Contains("Core Max")) { val = s.Value; return; }
                }
            }
            if (_computer != null) foreach (var hw in _computer.Hardware) { Scan(hw); foreach (var sub in hw.SubHardware) try { Scan(sub); } catch { } }
            return val;
        }

        List<MetricValue> ReadGpuTemps()
        {
            var list = new List<MetricValue>();
            SensorInfo target = null;
            if (!string.IsNullOrEmpty(GpuSensorName)) target = AvailableGpuSensors.FirstOrDefault(s => s.SensorName == GpuSensorName);

            foreach (var hw in _computer.Hardware)
            {
                if (hw.HardwareType != HardwareType.GpuNvidia && hw.HardwareType != HardwareType.GpuAmd && hw.HardwareType != HardwareType.GpuIntel) continue;
                foreach (var s in hw.Sensors)
                {
                    if (s.SensorType != SensorType.Temperature || !Valid(s.Value)) continue;

                    // 主 GPU 温度
                    if ((target == null && (s.Name.Contains("Core") && !s.Name.Contains("Hot") && !s.Name.Contains("Memory") && !s.Name.Contains("Junction"))) ||
                        (target != null && s.Name == target.SensorName))
                    {
                        if (!list.Any(m => m.Label == "GPU"))
                            list.Add(new MetricValue { Label = "GPU", Icon = "\U0001F3AE", Value = s.Value, Unit = "°C" });
                    }
                    // Hot Spot
                    if (AppPrefs.GetBool("Monitor.ShowGpuHotSpot", false) && s.Name.Contains("Hot Spot"))
                        list.Add(new MetricValue { Label = "Hot Spot", Icon = "\U0001F525", Value = s.Value, Unit = "°C" });
                    // Memory Junction
                    if (AppPrefs.GetBool("Monitor.ShowGpuMemory", false) && (s.Name.Contains("Memory") || s.Name.Contains("Junction")))
                        list.Add(new MetricValue { Label = "Mem", Icon = "\U0001F4BE", Value = s.Value, Unit = "°C" });
                }
            }
            return list;
        }

        float? ReadFan(string hwPrefix)
        {
            foreach (var s in AvailableFanSensors)
            {
                if (!s.HardwareName.ToLower().Contains(hwPrefix.ToLower())) continue;
                if (s.SensorType != SensorType.Fan && s.SensorType != SensorType.Control) continue;
                // 在 _computer 中查找对应 sensor 读值
                if (_computer == null) continue;
                foreach (var hw in _computer.Hardware)
                {
                    if (hw.Name != s.HardwareName) continue;
                    foreach (var ss in hw.Sensors)
                        if (ss.SensorType == SensorType.Fan && ss.Name == s.SensorName && ValidFan(ss.Value))
                            return ss.Value;
                }
            }
            return null;
        }

        static bool Valid(float? v) => v.HasValue && v.Value > 0 && v.Value < 150;
        static bool ValidFan(float? v) => v.HasValue && v.Value >= 0 && v.Value < 10000;

        public void Stop()
        {
            try { _computer?.Close(); } catch { }
            _computer = null; _started = false; _hwReady = false; IsAvailable = false;
            AvailableCpuSensors.Clear(); AvailableGpuSensors.Clear(); AvailableFanSensors.Clear();
            ActiveMetrics.Clear();
        }

        public void Dispose() => Stop();

        private class UpdateVisitor : IVisitor
        {
            public void VisitComputer(IComputer c) { c.Traverse(this); }
            public void VisitHardware(IHardware h) { h.Update(); foreach (var s in h.SubHardware) s.Accept(this); }
            public void VisitSensor(ISensor _) { }
            public void VisitParameter(IParameter _) { }
        }
    }
}
