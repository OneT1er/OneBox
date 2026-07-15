using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Windows.Media;

namespace PowerAudioManager
{
    /// <summary>
    /// 性能监控历史采样：按 ConfigKey 存最近 N 个值（环形 float[]，容量全天 86400 @1s）。
    /// 环形数组：Add 为 O(1)（替代 List.RemoveAt(0) 的 O(n)），内存固定无扩容。
    /// 采集温度（°C）与风扇（RPM）。退出写 JSON / 启动读 JSON，跨重启保留全天历史。
    /// </summary>
    public static class PerfHistory
    {
        public const int Capacity = 86400; // 全天 @1s
        static readonly object _lock = new object();
        static readonly Dictionary<string, Series> _series = new Dictionary<string, Series>();

        class Series
        {
            public float[] Buf = new float[Capacity];
            public int Head, Count;
            public string Name, Icon;
            public bool IsTemp;
        }

        // 8 色循环调色板，保证相邻线条颜色区分
        static readonly Color[] Palette =
        {
            Color.FromRgb(0x8E, 0x8C, 0xD8), // 紫影
            Color.FromRgb(0x4C, 0xC2, 0x7A), // 绿
            Color.FromRgb(0x6A, 0xB0, 0xE0), // 蓝
            Color.FromRgb(0xE0, 0xA8, 0x5A), // 橙
            Color.FromRgb(0xE0, 0x6C, 0x6C), // 红
            Color.FromRgb(0x4C, 0xD0, 0xD0), // 青
            Color.FromRgb(0xE0, 0xD0, 0x5A), // 黄
            Color.FromRgb(0xD8, 0x8C, 0xB8), // 粉
        };

        static string _path;
        static string FilePath
        {
            get
            {
                if (_path == null)
                {
                    var exe = Environment.ProcessPath;
                    string dir = string.IsNullOrEmpty(exe) ? AppDomain.CurrentDomain.BaseDirectory : Path.GetDirectoryName(exe);
                    _path = Path.Combine(dir, "OneBox.perfhistory.json");
                }
                return _path;
            }
        }

        public static void Add(List<MetricValue> metrics)
        {
            if (metrics == null) return;
            lock (_lock)
            {
                // 清理已删除的指标（用户在设置里删的，不再显示历史数据）
                if (metrics.Count > 0)
                {
                    var currentKeys = new HashSet<string>(metrics.Where(m => m.IsTemp || m.Unit == "RPM").Select(m => m.ConfigKey));
                    foreach (var k in _series.Keys.Where(k => !currentKeys.Contains(k)).ToList()) _series.Remove(k);
                }
                foreach (var m in metrics)
                {
                    bool isTemp = m.IsTemp;
                    bool isFan = m.Unit == "RPM";
                    if (!isTemp && !isFan) continue;
                    if (string.IsNullOrEmpty(m.ConfigKey)) continue;
                    if (!_series.TryGetValue(m.ConfigKey, out var s)) { s = new Series(); _series[m.ConfigKey] = s; }
                    if (m.Value.HasValue)
                    {
                        s.Buf[s.Head] = m.Value.Value;
                        s.Head = (s.Head + 1) % Capacity;
                        if (s.Count < Capacity) s.Count++;
                    }
                    s.Name = m.DisplayName;
                    s.Icon = m.IconKey;
                    s.IsTemp = isTemp;
                }
            }
        }

        // maxPoints=0 取全部；否则取最近 maxPoints 个
        public static List<ChartSeries> GetSeries(int maxPoints)
        {
            lock (_lock)
            {
                var result = new List<ChartSeries>();
                int idx = 0;
                foreach (var kv in _series.OrderBy(x => x.Key))   // 稳定顺序 -> 稳定配色
                {
                    var s = kv.Value;
                    if (s.Count == 0) { idx++; continue; }
                    int n = maxPoints > 0 && s.Count > maxPoints ? maxPoints : s.Count;
                    int start = (s.Head - n + Capacity) % Capacity;
                    var pts = new List<float>(n);
                    for (int i = 0; i < n; i++) pts.Add(s.Buf[(start + i) % Capacity]);
                    result.Add(new ChartSeries
                    {
                        Name = s.Name ?? kv.Key,
                        Color = Palette[idx % Palette.Length],
                        Points = pts,
                        IsTemp = s.IsTemp,
                        Unit = s.IsTemp ? "°C" : "rpm"
                    });
                    idx++;
                }
                return result;
            }
        }

        public static void Clear() { lock (_lock) _series.Clear(); }
        public static int SeriesCount { get { lock (_lock) return _series.Count; } }

        // ---- 持久化（退出写 / 启动读）----
        class SeriesData { public string name { get; set; } public string icon { get; set; } public bool isTemp { get; set; } public List<float> points { get; set; } }

        public static void Save()
        {
            try
            {
                Dictionary<string, SeriesData> data;
                lock (_lock)
                {
                    data = new Dictionary<string, SeriesData>();
                    foreach (var kv in _series)
                    {
                        var s = kv.Value;
                        var pts = new List<float>(s.Count);
                        int start = (s.Head - s.Count + Capacity) % Capacity;
                        for (int i = 0; i < s.Count; i++) pts.Add(s.Buf[(start + i) % Capacity]);
                        data[kv.Key] = new SeriesData { name = s.Name, icon = s.Icon, isTemp = s.IsTemp, points = pts };
                    }
                }
                File.WriteAllText(FilePath, JsonSerializer.Serialize(data));
                AppLog.Log("PerfHistory", "saved " + data.Count + " series");
            }
            catch (Exception ex) { AppLog.Log("PerfHistory", "save fail: " + ex.Message); }
        }

        public static void Load()
        {
            try
            {
                if (!File.Exists(FilePath)) return;
                var data = JsonSerializer.Deserialize<Dictionary<string, SeriesData>>(File.ReadAllText(FilePath));
                if (data == null) return;
                lock (_lock)
                {
                    foreach (var kv in data)
                    {
                        var s = new Series { Name = kv.Value.name, Icon = kv.Value.icon, IsTemp = kv.Value.isTemp };
                        if (kv.Value.points != null)
                            foreach (var v in kv.Value.points)
                            {
                                s.Buf[s.Head] = v; s.Head = (s.Head + 1) % Capacity;
                                if (s.Count < Capacity) s.Count++;
                            }
                        _series[kv.Key] = s;
                    }
                }
                AppLog.Log("PerfHistory", "loaded " + data.Count + " series");
            }
            catch (Exception ex) { AppLog.Log("PerfHistory", "load fail: " + ex.Message); }
        }
    }
}
