using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
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
        public string Label;     // "CPU", "GPU", etc.
        public string Icon;      // "🌡", "🎮", etc.
        public float? Value;
        public string Unit;      // "°C", "RPM"
        public bool IsTemp => Unit == "°C";

        // 从配置反序列化
        public string ConfigKey; // "Temp|AMD Ryzen 7 9800X3D|Core (Tctl/Tdie)"
    }

    public class HardwareMonitorService : IDisposable
    {
        public static readonly HardwareMonitorService Instance = new HardwareMonitorService();

        private Computer _computer;
        private bool _started, _hwReady;
        private readonly object _lock = new object();

        public bool IsAvailable { get; private set; }
        public float? CpuTemperature { get; private set; }
        public float? GpuTemperature { get; private set; }
        public List<SensorInfo> AllTempSensors { get; } = new();
        public List<SensorInfo> AllFanSensors { get; } = new();

        // 用户配置的指标（注册表持久化）
        public List<string> EnabledMetrics { get; private set; } = new();

        // 轮询后产生的值
        public List<MetricValue> ActiveMetrics { get; } = new();

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
                    LoadEnabledMetrics();
                    _hwReady = true;
                    IsAvailable = true;
                    AppLog.Log("Temp", $"ready: temp={AllTempSensors.Count} fan={AllFanSensors.Count} enabled={EnabledMetrics.Count} admin={AdminUtils.IsAdmin()}");
                }
                catch (Exception ex) { AppLog.Log("Temp", "init fail: " + ex.Message); _hwReady = false; }
            });
        }

        void DiscoverSensors()
        {
            AllTempSensors.Clear(); AllFanSensors.Clear();

            void Scan(IHardware hw)
            {
                foreach (var s in hw.Sensors)
                {
                    var info = new SensorInfo { HardwareName = hw.Name, SensorName = s.Name, HwType = hw.HardwareType, SensorType = s.SensorType };
                    if (s.SensorType == SensorType.Temperature)
                    {
                        AllTempSensors.Add(info);
                        AppLog.Log("Temp", $"  [T] {info}");
                    }
                    if (s.SensorType == SensorType.Fan || s.SensorType == SensorType.Control)
                    {
                        AllFanSensors.Add(info);
                        AppLog.Log("Temp", $"  [F] {info}");
                    }
                }
                foreach (var sub in hw.SubHardware) Scan(sub);
            }
            if (_computer != null) foreach (var hw in _computer.Hardware) Scan(hw);
        }

        void LoadEnabledMetrics()
        {
            var raw = AppPrefs.GetString("Monitor.Metrics", "");
            if (string.IsNullOrWhiteSpace(raw))
            {
                // 首次运行：自动添加 CPU + GPU 温度
                var cpuSensor = AllTempSensors.FirstOrDefault(s => s.HwType == HardwareType.Cpu);
                var gpuSensor = AllTempSensors.FirstOrDefault(s =>
                    s.HwType == HardwareType.GpuNvidia || s.HwType == HardwareType.GpuAmd || s.HwType == HardwareType.GpuIntel);
                var list = new List<string>();
                if (cpuSensor != null)
                    list.Add(EncodeConfig(cpuSensor));
                if (gpuSensor != null)
                    list.Add(EncodeConfig(gpuSensor));
                raw = string.Join(";", list);
                if (list.Count > 0) AppPrefs.SetString("Monitor.Metrics", raw);
            }
            EnabledMetrics = raw.Split(';', StringSplitOptions.RemoveEmptyEntries).Select(s => s.Trim()).Where(s => s.Length > 0).ToList();
        }

        public static string EncodeConfig(SensorInfo s)
        {
            string type = s.SensorType.ToString(); // "Temperature", "Fan", "Control"
            return $"{type}|{s.HardwareName}|{s.SensorName}";
        }

        public static SensorInfo DecodeConfig(string key)
        {
            var parts = key.Split('|');
            if (parts.Length < 3) return null;
            SensorType st;
            if (!Enum.TryParse(parts[0], out st)) st = SensorType.Temperature;
            return new SensorInfo { SensorType = st, HardwareName = parts[1], SensorName = parts[2] };
        }

        public void SaveEnabledMetrics(List<string> list)
        {
            EnabledMetrics = list;
            AppPrefs.SetString("Monitor.Metrics", string.Join(";", list));
        }

        // ---- 轮询 ----

        public void Update()
        {
            if (!IsAvailable || _computer == null || !_hwReady) return;
            try
            {
                _computer.Accept(new UpdateVisitor());
                var values = new List<MetricValue>();

                // 始终读 CPU / GPU 温度（折叠栏固定显示）
                CpuTemperature = ReadCpuTemp();
                GpuTemperature = ReadGpuTemp();

                foreach (var key in EnabledMetrics)
                {
                    var cfg = DecodeConfig(key);
                    if (cfg == null) continue;
                    var sensor = FindSensor(cfg);
                    if (sensor == null) continue;

                    float? val = ReadSensorValue(sensor);
                    if (val.HasValue)
                    {
                        string icon = AutoIcon(cfg);
                        string label = cfg.SensorName.Length > 10 ? cfg.SensorName.Substring(0, 10) : cfg.SensorName;
                        values.Add(new MetricValue
                        {
                            Label = label, Icon = icon, Value = val,
                            Unit = cfg.SensorType == SensorType.Fan ? "RPM" : "°C",
                            ConfigKey = key
                        });
                    }
                }

                lock (_lock) { ActiveMetrics.Clear(); ActiveMetrics.AddRange(values); }
            }
            catch { }
        }

        // 设置中预览传感器实时值（读取缓存值，不触发硬件刷新）
        public float? ReadSensorPreview(SensorInfo cfg)
        {
            if (_computer == null || !_hwReady) return null;
            try { return ReadSensorValue(cfg); }
            catch { return null; }
        }

        float? ReadCpuTemp()
        {
            var sensor = AllTempSensors.FirstOrDefault(s => s.HwType == HardwareType.Cpu);
            return sensor != null ? ReadSensorValue(sensor) : null;
        }

        float? ReadGpuTemp()
        {
            var sensor = AllTempSensors.FirstOrDefault(s =>
                s.HwType == HardwareType.GpuNvidia || s.HwType == HardwareType.GpuAmd || s.HwType == HardwareType.GpuIntel);
            if (sensor == null) return null;
            // 优先 GPU Core，排除 Hot Spot
            var coreSensor = AllTempSensors.FirstOrDefault(s =>
                s.HardwareName == sensor.HardwareName && s.SensorName.Contains("Core") && !s.SensorName.Contains("Hot"));
            return ReadSensorValue(coreSensor ?? sensor);
        }

        SensorInfo FindSensor(SensorInfo cfg)
        {
            var pool = cfg.SensorType == SensorType.Fan ? AllFanSensors : AllTempSensors;
            return pool.FirstOrDefault(s => s.HardwareName == cfg.HardwareName && s.SensorName == cfg.SensorName);
        }

        float? ReadSensorValue(SensorInfo cfg)
        {
            if (_computer == null) return null;
            return ReadSensorValueIn(_computer.Hardware, cfg);
        }

        float? ReadSensorValueIn(IList<IHardware> hardwareList, SensorInfo cfg)
        {
            foreach (var hw in hardwareList)
            {
                if (hw.Name == cfg.HardwareName)
                {
                    foreach (var s in hw.Sensors)
                    {
                        if (s.Name == cfg.SensorName && s.SensorType == cfg.SensorType)
                        {
                            float v = s.Value ?? float.NaN;
                            if (float.IsNaN(v)) return null;
                            if (cfg.SensorType == SensorType.Temperature && (v <= 0 || v > 150)) return null;
                            bool isFanType = cfg.SensorType == SensorType.Fan || cfg.SensorType == SensorType.Control;
                            if (isFanType && (v < 0 || v > 10000)) return null;
                            return v;
                        }
                    }
                }
                // 也搜子硬件
                if (hw.SubHardware != null && hw.SubHardware.Length > 0)
                {
                    var subResult = ReadSensorValueIn(hw.SubHardware, cfg);
                    if (subResult.HasValue) return subResult;
                }
            }
            return null;
        }

        public static string AutoIcon(SensorInfo cfg)
        {
            bool isCpu = cfg.HardwareName.ToLower().Contains("cpu") || cfg.HardwareName.ToLower().Contains("ryzen") || cfg.HardwareName.ToLower().Contains("intel");
            bool isGpu = cfg.HardwareName.ToLower().Contains("nvidia") || cfg.HardwareName.ToLower().Contains("amd radeon") || cfg.HardwareName.ToLower().Contains("geforce") || cfg.HardwareName.ToLower().Contains("rtx");
            string name = cfg.SensorName.ToLower();

            if (cfg.SensorType == SensorType.Temperature)
            {
                if (name.Contains("hot spot")) return "\U0001F525";
                if (name.Contains("memory") || name.Contains("junction")) return "\U0001F4BE";
                if (isCpu) return "\U0001F321";
                if (isGpu) return "\U0001F3AE";
                return "\U0001F321";
            }
            // Fan
            if (isCpu) return "\U0001F300";
            if (isGpu) return "\U0001F32A";
            return "\U0001F300";
        }

        public void Stop()
        {
            try { _computer?.Close(); } catch { }
            _computer = null; _started = false; _hwReady = false; IsAvailable = false;
            AllTempSensors.Clear(); AllFanSensors.Clear();
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
