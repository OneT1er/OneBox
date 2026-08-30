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
    /// 后台持续采集：无需打开图表即记录（数据常驻内存，全天容量固定）。
    /// 启动懒加载磁盘历史，每 60 秒自动落盘 + 退出落盘，崩溃最多丢 1 分钟；时间戳随值一并持久化。
    /// </summary>
    public static class PerfHistory
    {
        public const int Capacity = 86400; // 全天 @1s
        static readonly object _lock = new object();
        static readonly object _saveIoLock = new object();
        static readonly Dictionary<string, Series> _series = new Dictionary<string, Series>();
        // 后台持续采集：数据常驻内存（每条 series 全天约 1MB，传感器数量有限，可接受），
        // 每 60 秒自动原子落盘一次 + 退出落盘；主文件损坏时优先恢复上一代有效 .bak。
        static System.Threading.Timer _saveTimer;
        static bool _loadAttempted;   // Load 已尝试过（失败不每秒重试）
        static bool _loaded;          // _series 已从磁盘加载；Save 未加载时跳过，避免空写覆盖持久化数据
        static bool _preserveBackupOnNextSave;

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
                    string dir = Path.Combine(
                        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "OneT1er", "OneBox");
                    _path = Path.Combine(dir, "OneBox.perfhistory.json");
                }
                return _path;
            }
        }

        static List<string> GetLoadCandidates()
        {
            var result = new List<string>();
            void AddPair(string path)
            {
                if (string.IsNullOrEmpty(path)) return;
                if (!result.Contains(path, StringComparer.OrdinalIgnoreCase)) result.Add(path);
                string backup = path + ".bak";
                if (!result.Contains(backup, StringComparer.OrdinalIgnoreCase)) result.Add(backup);
            }

            AddPair(FilePath);

            // v1.8.1 and earlier kept history beside the executable. Keep both
            // the current process directory and the conventional Velopack
            // current directory as one-time migration sources.
            string exe = Environment.ProcessPath;
            string exeDir = string.IsNullOrEmpty(exe) ? AppDomain.CurrentDomain.BaseDirectory : Path.GetDirectoryName(exe);
            AddPair(Path.Combine(exeDir, "OneBox.perfhistory.json"));
            string localRoot = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "OneBox");
            AddPair(Path.Combine(localRoot, "OneBox.perfhistory.json"));
            AddPair(Path.Combine(localRoot, "current", "OneBox.perfhistory.json"));
            return result;
        }

        public static void Add(List<MetricValue> metrics)
        {
            if (metrics == null) return;
            EnsureLoaded();
            lock (_lock)
            {
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

        // 只有设置中明确删除/替换指标时才清理对应历史。单次快照缺值（DDR5
        // 冷启动、SMBus 抖动等）绝不能删除已经持久化的整条曲线。
        public static void RetainEnabledSeries(IEnumerable<string> enabledKeys)
        {
            EnsureLoaded();
            var enabled = new HashSet<string>(enabledKeys ?? Enumerable.Empty<string>(), StringComparer.Ordinal);
            lock (_lock)
            {
                foreach (string key in _series.Keys.Where(key => !enabled.Contains(key)).ToList())
                    _series.Remove(key);
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

        // 所有系列里最新一条记录的时间戳（无记录返回 null）。
        // 图表刚打开且窗口内无数据时，用它将时间窗锚到历史末尾，让以前记录的数据立即可见。
        public static DateTime? GetLastTime()
        {
            lock (_lock)
            {
                DateTime? last = null;
                foreach (var s in _series.Values)
                {
                    if (s.Count == 0) continue;
                    var t = s.Times[(s.Head - 1 + Capacity) % Capacity];
                    if (last == null || t > last.Value) last = t;
                }
                return last;
            }
        }

        public static int SeriesCount { get { lock (_lock) return _series.Count; } }

        // 性能趋势图窗口打开时调用：确保历史已从磁盘加载（后台采集常驻，无需引用计数）。
        public static void Acquire()
        {
            EnsureLoaded();
        }

        // 性能趋势图窗口关闭时调用：把当前数据落盘（内存保留，后台继续采集）。
        public static void Release()
        {
            try { Save(); } catch (Exception ex) { AppLog.Log("PerfHistory", ex); }
        }

        // 首次 Add/图表打开时懒加载磁盘历史；失败仅尝试一次，坏文件隔离后尝试有效备份。
        public static void EnsureLoaded()
        {
            if (_loaded || _loadAttempted) return;
            lock (_lock)
            {
                if (_loaded || _loadAttempted) return;
                _loadAttempted = true;
                try { _loaded = Load(); } catch (Exception ex) { AppLog.Log("PerfHistory", ex); }
                EnsureSaveTimer();
            }
        }

        // 后台定期落盘（60 秒），崩溃最多丢 1 分钟；退出由 ExitApp 的 Save 兜底。
        static void EnsureSaveTimer()
        {
            if (_saveTimer != null) return;
            _saveTimer = new System.Threading.Timer(_ =>
            {
                try { Save(); } catch { }
            }, null, TimeSpan.FromSeconds(60), TimeSpan.FromSeconds(60));
        }

        // ---- 持久化（退出写 / 启动读）----
        class SeriesData { public string name { get; set; } public string icon { get; set; } public bool isTemp { get; set; } public List<float> points { get; set; } public List<string> times { get; set; } }

        public static void Save()
        {
            try
            {
                lock (_saveIoLock)
                {
                    Dictionary<string, SeriesData> data;
                    bool preserveBackup;
                    lock (_lock)
                    {
                        if (!_loaded) return;   // 从未加载过磁盘数据：不空写覆盖持久化文件
                        preserveBackup = _preserveBackupOnNextSave;
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
                                times.Add(((DateTimeOffset)s.Times[(start + i) % Capacity].ToUniversalTime()).ToUnixTimeSeconds().ToString(CultureInfo.InvariantCulture));
                            }
                            data[kv.Key] = new SeriesData { name = s.Name, icon = s.Icon, isTemp = s.IsTemp, points = pts, times = times };
                        }
                    }
                    string json = JsonSerializer.Serialize(data);
                    DurableFileStore.WriteUtf8Atomically(FilePath, json, preserveBackup);
                    lock (_lock) _preserveBackupOnNextSave = false;
                    AppLog.Log("PerfHistory", "saved " + data.Count + " series");
                }
            }
            catch (Exception ex) { AppLog.Log("PerfHistory", "save fail: " + ex.Message); }
        }

        static Dictionary<string, Series> ReadSeries(string path, out string json)
        {
            json = File.ReadAllText(path);
            var data = JsonSerializer.Deserialize<Dictionary<string, SeriesData>>(json)
                ?? new Dictionary<string, SeriesData>();
            DateTime fileTime = File.GetLastWriteTime(path);
            var loaded = new Dictionary<string, Series>();
            foreach (var kv in data)
            {
                if (kv.Value == null) throw new InvalidDataException("Series data is missing for " + kv.Key);
                var s = new Series { Name = kv.Value.name, Icon = kv.Value.icon, IsTemp = kv.Value.isTemp };
                var pts = kv.Value.points;
                var times = kv.Value.times;
                if (pts != null)
                {
                    int first = Math.Max(0, pts.Count - Capacity);
                    for (int i = first; i < pts.Count; i++)
                    {
                        DateTime t = fileTime.AddSeconds(-(pts.Count - 1 - i));
                        if (times != null && i < times.Count)
                        {
                            string rawTime = times[i];
                            if (long.TryParse(rawTime, NumberStyles.Integer, CultureInfo.InvariantCulture, out long unixSec))
                            {
                                try { t = DateTimeOffset.FromUnixTimeSeconds(unixSec).LocalDateTime; }
                                catch (ArgumentOutOfRangeException) { }
                            }
                            else if (DateTime.TryParse(rawTime, null, DateTimeStyles.RoundtripKind, out DateTime parsed))
                            {
                                t = parsed.Kind == DateTimeKind.Utc ? parsed.ToLocalTime() : parsed;
                            }
                        }
                        s.Buf[s.Head] = pts[i];
                        s.Times[s.Head] = t;
                        s.Head = (s.Head + 1) % Capacity;
                        if (s.Count < Capacity) s.Count++;
                    }
                }
                loaded[kv.Key] = s;
            }
            return loaded;
        }

        // 主文件无效时依次尝试 last-known-good 备份和旧版 executable 目录，
        // 损坏文件单独隔离保留证据，绝不再覆盖有效 .bak。
        public static bool Load()
        {
            foreach (string candidate in GetLoadCandidates())
            {
                if (!File.Exists(candidate)) continue;
                try
                {
                    Dictionary<string, Series> loaded = ReadSeries(candidate, out string json);
                    lock (_lock)
                    {
                        _series.Clear();
                        foreach (var kv in loaded) _series[kv.Key] = kv.Value;
                    }

                    if (!string.Equals(candidate, FilePath, StringComparison.OrdinalIgnoreCase))
                    {
                        bool recoveredBackup = string.Equals(candidate, FilePath + ".bak", StringComparison.OrdinalIgnoreCase);
                        lock (_lock) _preserveBackupOnNextSave = recoveredBackup;
                        try
                        {
                            DurableFileStore.WriteUtf8Atomically(FilePath, json, preserveBackup: recoveredBackup);
                            lock (_lock) _preserveBackupOnNextSave = false;
                            AppLog.Log("PerfHistory", recoveredBackup
                                ? "restored last-known-good backup"
                                : "migrated history to stable app-data path");
                        }
                        catch (Exception ex)
                        {
                            AppLog.Log("PerfHistory", "history restore/migration deferred: " + ex.Message);
                        }
                    }

                    AppLog.Log("PerfHistory", "loaded " + loaded.Count + " series");
                    return true;
                }
                catch (Exception ex)
                {
                    AppLog.Log("PerfHistory", "load failed from " + candidate + ": " + ex.Message);
                    try
                    {
                        string quarantined = DurableFileStore.QuarantineCorruptFile(candidate);
                        if (!string.IsNullOrEmpty(quarantined))
                            AppLog.Log("PerfHistory", "quarantined corrupt history as " + quarantined);
                    }
                    catch (Exception quarantineError)
                    {
                        AppLog.Log("PerfHistory", "could not quarantine corrupt history: " + quarantineError.Message);
                    }
                }
            }
            return true;   // 无有效文件时按空历史继续，后续新数据正常原子落盘
        }
    }
}
