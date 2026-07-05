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
        public string DisplayName;
        public string IconKey;      // "cpu","gpu","hot","vram","fan","ctrl","def"
        public float? Value;
        public string Unit;         // "°C", "RPM", "%"
        public bool IsTemp => Unit == "°C";
        public string ConfigKey;
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
        public List<SensorInfo> AllFanSensors { get; } = new();      // Fan (RPM)
        public List<SensorInfo> AllControlSensors { get; } = new();   // Control (%)

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
            var seen = new HashSet<string>();

            void Scan(IHardware hw)
            {
                foreach (var s in hw.Sensors)
                {
                    var key = $"{hw.Name}|{s.Name}|{s.SensorType}";
                    if (!seen.Add(key)) continue; // 去重：不同层级可能有同名传感器

                    var info = new SensorInfo { HardwareName = hw.Name, SensorName = s.Name, HwType = hw.HardwareType, SensorType = s.SensorType };
                    if (s.SensorType == SensorType.Temperature)
                    {
                        AllTempSensors.Add(info);
                        AppLog.Log("Temp", $"  [T] {info} ({s.Value?.ToString("0") ?? "null"})");
                    }
                    if (s.SensorType == SensorType.Fan)
                    {
                        AllFanSensors.Add(info);
                        AppLog.Log("Temp", $"  [FAN] {info} val={s.Value?.ToString("0") ?? "null"} RPM");
                    }
                    if (s.SensorType == SensorType.Control)
                    {
                        AllControlSensors.Add(info);
                        AppLog.Log("Temp", $"  [CTRL] {info} val={s.Value?.ToString("0") ?? "null"} %");
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
                    list.Add(EncodeConfig(cpuSensor, "CPU"));
                if (gpuSensor != null)
                    list.Add(EncodeConfig(gpuSensor, "GPU"));
                raw = string.Join(";", list);
                if (list.Count > 0) AppPrefs.SetString("Monitor.Metrics", raw);
            }
            EnabledMetrics = raw.Split(';', StringSplitOptions.RemoveEmptyEntries).Select(s => s.Trim()).Where(s => s.Length > 0).ToList();
        }

        public static string EncodeConfig(SensorInfo s, string displayName) => EncodeConfig(s, displayName, AutoIconKey(displayName, s));

        public static string EncodeConfig(SensorInfo s, string displayName, string iconKey)
        {
            string type = s.SensorType.ToString();
            return $"{type}|{s.HardwareName}|{s.SensorName}|{displayName}|{iconKey}";
        }

        public static SensorInfo DecodeConfig(string key, out string displayName, out string iconKey)
        {
            displayName = ""; iconKey = "def";
            var parts = key.Split('|');
            if (parts.Length < 3) return null;
            SensorType st;
            if (!Enum.TryParse(parts[0], out st)) st = SensorType.Temperature;
            displayName = parts.Length >= 4 ? parts[3] : DefaultDisplayName(parts[1], parts[2], st);
            iconKey = parts.Length >= 5 ? parts[4] : AutoIconKey(displayName, null);
            return new SensorInfo { SensorType = st, HardwareName = parts[1], SensorName = parts[2] };
        }

        // 从 displayName + sensorInfo 推断默认图标 key
        public static string AutoIconKey(string displayName, SensorInfo s)
        {
            string dn = (displayName ?? "").ToLower();
            string hw = (s?.HardwareName ?? "").ToLower();
            string sn = (s?.SensorName ?? "").ToLower();

            if (s != null)
            {
                if (s.SensorType == SensorType.Fan) return "fan";
                if (s.SensorType == SensorType.Control) return "ctrl";
                if (sn.Contains("hot spot")) return "hot";
                if (sn.Contains("memory") || sn.Contains("junction")) return "vram";
                // 硬件名推断
                if (hw.Contains("memory") || hw.Contains("dram") || hw.Contains("dim") || hw.Contains("ram")) return "dram";
                if (hw.Contains("ssd") || hw.Contains("hdd") || hw.Contains("nvme") || hw.Contains("disk") || hw.Contains("stor")) return "disk";
                if (hw.Contains("motherboard") || hw.Contains("super") || hw.Contains("nuvoton") || hw.Contains("ite ")) return "mb";
            }
            if (dn.Contains("cpu") && !dn.Contains("fan")) return "cpu";
            if (dn.Contains("gpu") && !dn.Contains("hot") && !dn.Contains("vram") && !dn.Contains("mem") && !dn.Contains("fan")) return "gpu";
            if (dn.Contains("hot")) return "hot";
            if (dn.Contains("vram") || dn.Contains("显存")) return "vram";
            if (dn.Contains("内存") || dn.Contains("dram") || dn.Contains("ram")) return "dram";
            if (dn.Contains("硬盘") || dn.Contains("磁盘") || dn.Contains("ssd") || dn.Contains("disk")) return "disk";
            if (dn.Contains("主板") || dn.Contains("mb")) return "mb";
            if (dn.Contains("fan") && !dn.Contains("control") && !dn.Contains("%")) return "fan";
            if (dn.Contains("%") || dn.Contains("control")) return "ctrl";
            return "def";
        }

        public static string DefaultDisplayName(string hwName, string sensorName, SensorType st)
        {
            bool isCpu = hwName.ToLower().Contains("cpu") || hwName.ToLower().Contains("ryzen");
            bool isGpu = hwName.ToLower().Contains("nvidia") || hwName.ToLower().Contains("geforce") || hwName.ToLower().Contains("rtx") || hwName.ToLower().Contains("radeon");

            if (st == SensorType.Temperature)
            {
                if (sensorName.Contains("Hot Spot")) return "GPU HotSpot";
                if (sensorName.Contains("Memory") || sensorName.Contains("Junction")) return "VRAM";
                if (isCpu) return "CPU";
                if (isGpu) return "GPU";
                return "Temp";
            }
            if (st == SensorType.Fan)
            {
                if (isCpu) return "CPU Fan";
                if (isGpu) return "GPU Fan";
                return sensorName;
            }
            if (st == SensorType.Control)
            {
                if (isCpu) return "CPU Fan%";
                if (isGpu) return "GPU Fan%";
                return sensorName;
            }
            return sensorName;
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
                    string displayName, iconKey;
                    var cfg = DecodeConfig(key, out displayName, out iconKey);
                    if (cfg == null) continue;
                    var sensor = FindSensor(cfg);
                    if (sensor == null) continue;

                    float? val = ReadSensorValue(sensor);
                    if (val.HasValue)
                    {
                        string unit = cfg.SensorType == SensorType.Temperature ? "°C" :
                                      cfg.SensorType == SensorType.Control ? "%" : "RPM";
                        values.Add(new MetricValue { DisplayName = displayName, IconKey = iconKey, Value = val, Unit = unit, ConfigKey = key });
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
            List<SensorInfo> pool = cfg.SensorType switch
            {
                SensorType.Fan => AllFanSensors,
                SensorType.Control => AllControlSensors,
                _ => AllTempSensors
            };
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
                            if (cfg.SensorType == SensorType.Control && (v < 0 || v > 100)) return null;
                            if (cfg.SensorType == SensorType.Fan && (v < 0 || v > 10000)) return null;
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
            AllTempSensors.Clear(); AllFanSensors.Clear(); AllControlSensors.Clear();
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
