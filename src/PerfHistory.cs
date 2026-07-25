using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Windows.Media;

namespace PowerAudioManager
{
    /// <summary>
    /// 性能监控历史采样：按 ConfigKey 存最近 N 个 (值, 时间戳)（环形数组，容量全天 86400 @1s）。
    /// 环形数组：Add 为 O(1)（替代 List.RemoveAt(0) 的 O(n)），内存固定无扩容。
    /// 采集温度（°C）与风扇（RPM）。仅存真实读数（Cached 兜底值跳过），故传感器失配/跨重启缺口在图表上断线而非填旧值。
    /// 退出写 JSON / 启动读 JSON，跨重启保留全天历史；时间戳随值一并持久化。
    /// </summary>
    public static class PerfHistory
    {
        public const int Capacity = 86400; // 全天 @1s
        static readonly object _lock = new object();
        static readonly Dictionary<string, Series> _series = new Dictionary<string, Series>();

        class Series
        {
            public float[] Buf = new float[Capacity];
            public DateTime[] Times = new DateTime[Capacity];
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
                var now = DateTime.Now;
                foreach (var m in metrics)
                {
                    bool isTemp = m.IsTemp;
                    bool isFan = m.Unit == "RPM";
                    if (!isTemp && !isFan) continue;
                    if (string.IsNullOrEmpty(m.ConfigKey)) continue;
                    if (!_series.TryGetValue(m.ConfigKey, out var s)) { s = new Series(); _series[m.ConfigKey] = s; }
                    // 仅存真实读数；Cached（兜底旧值）跳过，历史图表在缺口处断线而非填旧值
                    if (m.Value.HasValue && !m.Cached)
                    {
                        s.Buf[s.Head] = m.Value.Value;
                        s.Times[s.Head] = now;
                        s.Head = (s.Head + 1) % Capacity;
                        if (s.Count < Capacity) s.Count++;
                    }
                    s.Name = m.DisplayName;
                    s.Icon = m.IconKey;
                    s.IsTemp = isTemp;
                }
            }
        }

        // 返回 [from, to] 时间窗内的点（带时间戳），供图表按真实时间定位、缺口断线。
        public static List<ChartSeries> GetSeries(DateTime from, DateTime to)
        {
            lock (_lock)
            {
                var result = new List<ChartSeries>();
                int idx = 0;
                foreach (var kv in _series.OrderBy(x => x.Key))   // 稳定顺序 -> 稳定配色
                {
                    var s = kv.Value;
                    if (s.Count == 0) { idx++; continue; }
                    var pts = new List<float>();
                    var times = new List<DateTime>();
                    for (int i = 0; i < s.Count; i++)
                    {
                        int bi = (s.Head - s.Count + i + Capacity) % Capacity;
                        var t = s.Times[bi];
                        if (t < from || t > to) continue;
                        pts.Add(s.Buf[bi]);
                        times.Add(t);
                    }
                    if (pts.Count == 0) { idx++; continue; }
                    result.Add(new ChartSeries
                    {
                        Name = s.Name ?? kv.Key,
                        Color = Palette[idx % Palette.Length],
                        Points = pts,
                        Times = times,
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
        class SeriesData { public string name { get; set; } public string icon { get; set; } public bool isTemp { get; set; } public List<float> points { get; set; } public List<string> times { get; set; } }

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
                        var times = new List<string>(s.Count);
                        int start = (s.Head - s.Count + Capacity) % Capacity;
                        for (int i = 0; i < s.Count; i++)
                        {
                            pts.Add(s.Buf[(start + i) % Capacity]);
                            times.Add(s.Times[(start + i) % Capacity].ToString("o", CultureInfo.InvariantCulture));
                        }
                        data[kv.Key] = new SeriesData { name = s.Name, icon = s.Icon, isTemp = s.IsTemp, points = pts, times = times };
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
                // 旧版 JSON 无 times：用文件最后修改时间回填（数据结束于保存时刻），避免旧数据被当成"最近"绘制
                DateTime fileTime = File.GetLastWriteTime(FilePath);
                lock (_lock)
                {
                    foreach (var kv in data)
                    {
                        var s = new Series { Name = kv.Value.name, Icon = kv.Value.icon, IsTemp = kv.Value.isTemp };
                        var pts = kv.Value.points;
                        var times = kv.Value.times;
                        if (pts != null)
                        {
                            for (int i = 0; i < pts.Count; i++)
                            {
                                DateTime t;
                                if (times != null && i < times.Count && DateTime.TryParse(times[i], null, DateTimeStyles.RoundtripKind, out var parsed))
                                    t = parsed;
                                else
                                    t = fileTime.AddSeconds(-(pts.Count - 1 - i)); // 旧格式回填：1s 间距结束于保存时刻
                                s.Buf[s.Head] = pts[i];
                                s.Times[s.Head] = t;
                                s.Head = (s.Head + 1) % Capacity;
                                if (s.Count < Capacity) s.Count++;
                            }
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
