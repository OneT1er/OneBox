using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Pipes;
using System.Linq;
using System.Text;
using System.Text.Json;
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

        // 上次成功读到的值（按 ConfigKey 缓存）：传感器偶发失配/SMBus 抖动时保留，避免 UI 闪烁或消失
        private readonly Dictionary<string, MetricValue> _lastMetrics = new();
        // 已记录过的传感器重映射，避免每秒轮询刷日志
        private readonly HashSet<string> _loggedRemaps = new();
        private List<MetricValue> _allPipeMetrics = new List<MetricValue>();

        private HardwareMonitorService() { }

        public void Start()
        {
            if (_started) return;
            lock (_lock) { if (_started) return; _started = true; }

            // 非管理员：温度由服务（OneBoxSvc）的 --temp-monitor helper 经 Global 管道推送，无 UAC
            if (!AdminUtils.IsAdmin())
            {
                LoadEnabledMetrics();  // 加载用户配的指标（UpdateFromPipe 过滤用），否则重启后指标为空
                StartPipeClient();
                return;
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
                        IsMemoryEnabled = true,
                        IsStorageEnabled = true,
                    };
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

        // ---- 非管理员：通过 admin helper（OneBox.exe --temp-monitor）命名管道读温度 ----
        void StartPipeClient()
        {
            System.Threading.ThreadPool.QueueUserWorkItem(_ =>
            {
                // 重试连 Global 管道，等服务（OneBoxSvc）的 --temp-monitor 就绪（最多 ~25s）
                NamedPipeClientStream client = null;
                for (int i = 0; i < 25; i++)
                {
                    try
                    {
                        client = new NamedPipeClientStream(".", "Global\\OneBox\\TempMonitor", PipeDirection.In);
                        client.Connect(1000);
                        break;
                    }
                    catch { try { client?.Dispose(); } catch { } client = null; System.Threading.Thread.Sleep(1000); }
                }
                if (client == null || !client.IsConnected)
                {
                    AppLog.Log("Temp", "pipe connect fail (service not running?)");
                    try { client?.Dispose(); } catch { }
                    return;
                }
                AppLog.Log("Temp", "pipe connected");
                ReadPipeLoop(client);
            });
        }

        void ReadPipeLoop(NamedPipeClientStream client)
        {
            if (client == null) return;
            try
            {
                using (var sr = new StreamReader(client, System.Text.Encoding.UTF8))
                {
                    while (client.IsConnected)
                    {
                        string line = sr.ReadLine();
                        if (line == null) break;
                        try { ParseAndFill(line); } catch { }
                    }
                }
            }
            catch (Exception ex) { AppLog.Log("Temp", "pipe read err: " + ex.Message); }
            AppLog.Log("Temp", "pipe disconnected");
        }

        void ParseAndFill(string json)
        {
            try
            {
                var p = JsonSerializer.Deserialize<TempPayload>(json);
                if (p == null) return;
                lock (_lock)
                {
                    CpuTemperature = p.cpu;
                    GpuTemperature = p.gpu;
                    IsAvailable = p.ready;
                    // 所有传感器值（helper 推送，OneBox 用用户 EnabledMetrics 过滤）
                    _allPipeMetrics.Clear();
                    if (p.allMetrics != null)
                        foreach (var m in p.allMetrics)
                            _allPipeMetrics.Add(new MetricValue { DisplayName = m.name, IconKey = m.icon, Value = m.value, Unit = m.unit, ConfigKey = m.key });
                    AllTempSensors.Clear();
                    if (p.sensors != null) foreach (var s in p.sensors) AllTempSensors.Add(new SensorInfo { HardwareName = s.hw, SensorName = s.name, HwType = ParseHw(s.hwtype), SensorType = ParseSt(s.stype) });
                    AllFanSensors.Clear();
                    if (p.fans != null) foreach (var s in p.fans) AllFanSensors.Add(new SensorInfo { HardwareName = s.hw, SensorName = s.name, HwType = ParseHw(s.hwtype), SensorType = ParseSt(s.stype) });
                    AllControlSensors.Clear();
                    if (p.controls != null) foreach (var s in p.controls) AllControlSensors.Add(new SensorInfo { HardwareName = s.hw, SensorName = s.name, HwType = ParseHw(s.hwtype), SensorType = ParseSt(s.stype) });
                }
            }
            catch { }
        }

        static HardwareType ParseHw(string s) => Enum.TryParse<HardwareType>(s, out var v) ? v : default;
        static SensorType ParseSt(string s) => Enum.TryParse<SensorType>(s, out var v) ? v : SensorType.Temperature;

        class TempPayload { public float? cpu { get; set; } public float? gpu { get; set; } public bool ready { get; set; } public List<TempMetric> metrics { get; set; } public List<TempMetric> allMetrics { get; set; } public List<TempSensor> sensors { get; set; } public List<TempSensor> fans { get; set; } public List<TempSensor> controls { get; set; } }
        class TempMetric { public string name { get; set; } public string icon { get; set; } public float? value { get; set; } public string unit { get; set; } public string key { get; set; } }
        class TempSensor { public string hw { get; set; } public string name { get; set; } public string hwtype { get; set; } public string stype { get; set; } }

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
                        // 过滤阈值/元数据传感器（值恒为 0 或固定阈值，非实时温度）：内存 SPD 的
                        // Thermal Sensor Low/High/Critical Limit、Temperature Sensor Resolution，
                        // 以及 SSD 的 Warning/Critical Temperature。避免污染选择列表与误选。
                        string sn = (s.Name ?? "").ToLower();
                        if (sn.Contains("resolution") || sn.Contains("limit") || sn.Contains("warning") || sn.Contains("critical"))
                            continue;
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
            if (!IsAvailable) return;
            // 非管理员：数据由服务 helper 经管道推送，用用户 EnabledMetrics 过滤 _allPipeMetrics
            if (_computer == null || !_hwReady) { UpdateFromPipe(); return; }
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
                    float? val = sensor != null ? ReadSensorValue(sensor) : null;

                    if (val.HasValue)
                    {
                        string unit = cfg.SensorType == SensorType.Temperature ? "°C" :
                                      cfg.SensorType == SensorType.Control ? "%" : "RPM";
                        var mv = new MetricValue { DisplayName = displayName, IconKey = iconKey, Value = val, Unit = unit, ConfigKey = key };
                        lock (_lock) { _lastMetrics[key] = mv; }
                        values.Add(mv);
                    }
                    else
                    {
                        lock (_lock) { if (_lastMetrics.TryGetValue(key, out var last)) values.Add(last); }
                    }
                }

                lock (_lock) { ActiveMetrics.Clear(); ActiveMetrics.AddRange(values); }
            }
            catch { }
        }

        // 非管理员：用用户 EnabledMetrics 过滤服务 helper 推送的所有传感器值
        void UpdateFromPipe()
        {
            var values = new List<MetricValue>();
            List<MetricValue> all;
            lock (_lock) all = new List<MetricValue>(_allPipeMetrics);
            foreach (var key in EnabledMetrics)
            {
                string displayName, iconKey;
                var cfg = DecodeConfig(key, out displayName, out iconKey);
                if (cfg == null) continue;
                var m = all.FirstOrDefault(x => x.ConfigKey == key);
                if (m == null)
                {
                    // fallback：按 SensorName+SensorType 匹配（DDR5 名字漂移），仅当唯一避免串台
                    var nm = all.Where(x => { var p = x.ConfigKey.Split('|'); return p.Length >= 3 && p[2] == cfg.SensorName && p[0] == cfg.SensorType.ToString(); }).ToList();
                    if (nm.Count == 1) m = nm[0];
                }
                if (m != null && m.Value.HasValue)
                {
                    var mv = new MetricValue { DisplayName = displayName, IconKey = iconKey, Value = m.Value, Unit = m.Unit, ConfigKey = key };
                    lock (_lock) { _lastMetrics[key] = mv; }
                    values.Add(mv);
                }
                else { lock (_lock) { if (_lastMetrics.TryGetValue(key, out var last)) values.Add(last); } }
            }
            lock (_lock) { ActiveMetrics.Clear(); ActiveMetrics.AddRange(values); }
        }

        // 设置中预览传感器实时值（读取缓存值，不触发硬件刷新）
        public float? ReadSensorPreview(SensorInfo cfg)
        {
            if (_computer != null && _hwReady)
            {
                try
                {
                    var sensor = FindSensor(cfg) ?? cfg;
                    return ReadSensorValue(sensor);
                }
                catch { return null; }
            }
            // 非管理员：从管道推送的所有传感器值找匹配
            if (cfg == null) return null;
            try
            {
                lock (_lock)
                {
                    foreach (var m in _allPipeMetrics)
                    {
                        var p = m.ConfigKey.Split('|');
                        if (p.Length >= 3 && p[0] == cfg.SensorType.ToString() && p[1] == cfg.HardwareName && p[2] == cfg.SensorName) return m.Value;
                    }
                }
            }
            catch { }
            return null;
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

        // 读取所有温度/风扇/控制传感器的 MetricValue（admin 模式，供 helper 推送给普通 OneBox 过滤）
        public List<MetricValue> ReadAllMetrics()
        {
            var list = new List<MetricValue>();
            if (_computer == null || !_hwReady) return list;
            try
            {
                void Scan(IList<IHardware> hws)
                {
                    foreach (var hw in hws)
                    {
                        foreach (var s in hw.Sensors)
                        {
                            if (s.SensorType != SensorType.Temperature && s.SensorType != SensorType.Fan && s.SensorType != SensorType.Control) continue;
                            if (!s.Value.HasValue) continue;
                            float v = s.Value.Value;
                            if (s.SensorType == SensorType.Temperature && (v <= 0 || v > 150)) continue;
                            var info = new SensorInfo { HardwareName = hw.Name, SensorName = s.Name, HwType = hw.HardwareType, SensorType = s.SensorType };
                            string dn = DefaultDisplayName(hw.Name, s.Name, s.SensorType);
                            string ik = AutoIconKey(dn, info);
                            string unit = s.SensorType == SensorType.Temperature ? "°C" : s.SensorType == SensorType.Control ? "%" : "RPM";
                            list.Add(new MetricValue { DisplayName = dn, IconKey = ik, Value = v, Unit = unit, ConfigKey = EncodeConfig(info, dn, ik) });
                        }
                        if (hw.SubHardware != null && hw.SubHardware.Length > 0) Scan(hw.SubHardware);
                    }
                }
                Scan(_computer.Hardware);
            }
            catch { }
            return list;
        }

        SensorInfo FindSensor(SensorInfo cfg)
        {
            List<SensorInfo> pool = cfg.SensorType switch
            {
                SensorType.Fan => AllFanSensors,
                SensorType.Control => AllControlSensors,
                _ => AllTempSensors
            };
            if (pool.Count == 0) return null;

            // L1: 精确匹配 HardwareName + SensorName（CPU/GPU/SSD 名字稳定，走这条）
            var exact = pool.FirstOrDefault(s => s.HardwareName == cfg.HardwareName && s.SensorName == cfg.SensorName);
            if (exact != null) return exact;

            // L2: DDR5 内存走 SMBus 解析 SPD 型号字符串，硬件名带乱码且每次启动漂移，L1 必然失配。
            //     若该 SensorName 在当前 pool 里唯一，则忽略 HardwareName 按 SensorName 匹配；
            //     SensorName 不唯一（如多盘 Temperature #1）则跳过，避免串台。
            var nameMatches = pool.Where(s => s.SensorName == cfg.SensorName).ToList();
            if (nameMatches.Count == 1)
            {
                LogRemap(cfg, nameMatches[0]);
                return nameMatches[0];
            }

            // L3: 内存 DIMM 编号也会漂移（#1<->#3，且有时只枚举到一条），兜底取任意一条内存温度。
            string sn = (cfg.SensorName ?? "").ToLower();
            if (sn.Contains("dimm") || sn.Contains("memory"))
            {
                var anyMem = pool.FirstOrDefault(s => s.HwType == HardwareType.Memory);
                if (anyMem != null)
                {
                    LogRemap(cfg, anyMem);
                    return anyMem;
                }
            }

            return null;
        }

        // 记录传感器重映射（每条配置只记一次，避免每秒轮询刷日志）
        void LogRemap(SensorInfo cfg, SensorInfo actual)
        {
            lock (_lock)
            {
                if (_loggedRemaps.Add(cfg.SensorName + "|" + cfg.HardwareName))
                    AppLog.Log("Temp", $"sensor remap: '{cfg.HardwareName} | {cfg.SensorName}' -> '{actual.HardwareName} | {actual.SensorName}'");
            }
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
            lock (_lock) { ActiveMetrics.Clear(); _lastMetrics.Clear(); _loggedRemaps.Clear(); }
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
