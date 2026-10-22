using System;
using System.Buffers;
using System.Buffers.Text;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Windows.Media;

namespace PowerAudioManager
{
    /// <summary>
    /// 性能监控历史采样：按 ConfigKey 存最近 N 个 (值, 时间戳)（环形数组，容量全天 86400 @1s）。
    /// 环形数组按 3600 点分块、写到才分配：Add 为 O(1)，新传感器几乎不占内存，满一天约 0.7MB/条。
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
        // 后台持续采集：数据常驻内存（分块按需分配，满一天约 0.7MB/条），
        // 每 60 秒自动原子落盘一次 + 退出落盘；主文件损坏时优先恢复上一代有效 .bak。
        static System.Threading.Timer _saveTimer;
        static bool _loadAttempted;   // Load 已尝试过（失败不每秒重试）
        static bool _loaded;          // _series 已从磁盘加载；Save 未加载时跳过，避免空写覆盖持久化数据
        static bool _preserveBackupOnNextSave;
        static bool _loggedSavedFile;

        // Samples are kept in 3600-sample chunks that are allocated only when
        // the ring first reaches them, so a freshly added sensor costs a few
        // hundred bytes instead of a pre-sized full day.  Each chunk stays
        // below the 85 KB large-object threshold.  Timestamps are whole UTC
        // seconds stored as an int offset from 2020-01-01, which is exactly
        // the precision the history file persists.
        internal const int ChunkSize = 3600;
        const int ChunkCount = Capacity / ChunkSize;
        const long StorageEpochUnix = 1577836800; // 2020-01-01T00:00:00Z

        sealed class Series
        {
            public readonly float[][] Values = new float[ChunkCount][];
            public readonly int[][] Seconds = new int[ChunkCount][];
            public int Head, Count;
            public string Name, Icon;
            public bool IsTemp;

            public void Append(float value, long unixSeconds)
            {
                int chunk = Head / ChunkSize, offset = Head % ChunkSize;
                if (Values[chunk] == null)
                {
                    Values[chunk] = new float[ChunkSize];
                    Seconds[chunk] = new int[ChunkSize];
                }
                Values[chunk][offset] = value;
                Seconds[chunk][offset] = ToStoredSeconds(unixSeconds);
                Head = (Head + 1) % Capacity;
                if (Count < Capacity) Count++;
            }

            // i-th oldest retained sample → ring position.
            public int RingIndex(int i) => (Head - Count + i + Capacity) % Capacity;
            public float ValueAt(int ring) => Values[ring / ChunkSize][ring % ChunkSize];
            public long UnixAt(int ring) => StorageEpochUnix + Seconds[ring / ChunkSize][ring % ChunkSize];
            public int AllocatedChunks
            {
                get { int n = 0; foreach (var c in Values) if (c != null) n++; return n; }
            }
        }

        static int ToStoredSeconds(long unixSeconds)
        {
            long relative = unixSeconds - StorageEpochUnix;
            if (relative > int.MaxValue) return int.MaxValue;
            if (relative < int.MinValue) return int.MinValue;
            return (int)relative;
        }

        // UTC seconds → local DateTime.  The zone offset is cached per
        // 15-minute UTC bucket (every real DST transition falls on such a
        // boundary), so converting a full day of samples costs one zone
        // lookup per bucket instead of one per sample.  Callers hold _lock.
        static long _offsetBucket = long.MinValue;
        static long _offsetTicks;
        static TimeZoneInfo _offsetZone;
        static DateTime UnixToLocal(long unixSeconds)
        {
            const long MinUnix = -62135596800L, MaxUnix = 253402300799L;
            if (unixSeconds <= MinUnix) return DateTime.MinValue;
            if (unixSeconds >= MaxUnix) return DateTime.MaxValue;
            long utcTicks = DateTime.UnixEpoch.Ticks + unixSeconds * TimeSpan.TicksPerSecond;
            long bucket = unixSeconds >= 0 ? unixSeconds / 900 : (unixSeconds - 899) / 900;
            TimeZoneInfo zone = TimeZoneInfo.Local;
            if (bucket != _offsetBucket || !ReferenceEquals(zone, _offsetZone))
            {
                _offsetTicks = zone.GetUtcOffset(new DateTime(utcTicks, DateTimeKind.Utc)).Ticks;
                _offsetBucket = bucket;
                _offsetZone = zone;
            }
            long ticks = utcTicks + _offsetTicks;
            if (ticks < DateTime.MinValue.Ticks) return DateTime.MinValue;
            if (ticks > DateTime.MaxValue.Ticks) return DateTime.MaxValue;
            return new DateTime(ticks, DateTimeKind.Local);
        }

        static long ToUnixSeconds(DateTime time)
        {
            DateTime utc = time.Kind == DateTimeKind.Utc ? time : time.ToUniversalTime();
            return new DateTimeOffset(utc).ToUnixTimeSeconds();
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
                long now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
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
                        s.Append(m.Value.Value, now);
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
                        int bi = s.RingIndex(i);
                        var t = UnixToLocal(s.UnixAt(bi));
                        if (t < from || t > to) continue;
                        pts.Add(s.ValueAt(bi));
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
                    var t = UnixToLocal(s.UnixAt((s.Head - 1 + Capacity) % Capacity));
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
        class SeriesSnapshot { public string Name; public string Icon; public bool IsTemp; public float[] Points; public int[] Times; }

        public static void Save()
        {
            try
            {
                lock (_saveIoLock)
                {
                    Dictionary<string, SeriesSnapshot> data;
                    bool preserveBackup;
                    lock (_lock)
                    {
                        if (!_loaded) return;   // 从未加载过磁盘数据：不空写覆盖持久化文件
                        preserveBackup = _preserveBackupOnNextSave;
                        data = new Dictionary<string, SeriesSnapshot>(_series.Count);
                        foreach (var kv in _series)
                        {
                            var s = kv.Value;
                            var pts = new float[s.Count];
                            var times = new int[s.Count];
                            for (int i = 0; i < s.Count; i++)
                            {
                                int index = s.RingIndex(i);
                                pts[i] = s.Values[index / ChunkSize][index % ChunkSize];
                                times[i] = s.Seconds[index / ChunkSize][index % ChunkSize];
                            }
                            data[kv.Key] = new SeriesSnapshot { Name = s.Name, Icon = s.Icon, IsTemp = s.IsTemp, Points = pts, Times = times };
                        }
                    }
                    DurableFileStore.WriteUtf8Atomically(FilePath, stream =>
                    {
                        using (var writer = new Utf8JsonWriter(stream))
                        {
                            Span<char> timestamp = stackalloc char[20];
                            writer.WriteStartObject();
                            foreach (var kv in data)
                            {
                                var s = kv.Value;
                                writer.WritePropertyName(kv.Key);
                                writer.WriteStartObject();
                                writer.WriteString("name", s.Name);
                                writer.WriteString("icon", s.Icon);
                                writer.WriteBoolean("isTemp", s.IsTemp);
                                writer.WriteStartArray("points");
                                foreach (float point in s.Points)
                                {
                                    writer.WriteNumberValue(point);
                                    if (writer.BytesPending >= 32 * 1024) writer.Flush();
                                }
                                writer.WriteEndArray();
                                writer.WriteStartArray("times");
                                foreach (int stored in s.Times)
                                {
                                    long time = StorageEpochUnix + stored;
                                    if (!time.TryFormat(timestamp, out int charsWritten, provider: CultureInfo.InvariantCulture))
                                        throw new InvalidOperationException("Could not format a history timestamp.");
                                    writer.WriteStringValue(timestamp[..charsWritten]);
                                    if (writer.BytesPending >= 32 * 1024) writer.Flush();
                                }
                                writer.WriteEndArray();
                                writer.WriteEndObject();
                            }
                            writer.WriteEndObject();
                        }
                    }, preserveBackup);
                    lock (_lock) _preserveBackupOnNextSave = false;
                    if (!_loggedSavedFile)
                    {
                        _loggedSavedFile = true;
                        try
                        {
                            var file = new FileInfo(FilePath);
                            AppLog.Log("PerfHistory", $"saved file pid={Environment.ProcessId} path={file.FullName} bytes={file.Length} lastWriteUtc={file.LastWriteTimeUtc:o}");
                        }
                        catch (Exception ex) { AppLog.Log("PerfHistory", "saved file metadata unavailable: " + ex.Message); }
                    }
                    AppLog.Log("PerfHistory", "saved " + data.Count + " series");
                }
            }
            catch (Exception ex) { AppLog.Log("PerfHistory", "save fail: " + ex.Message); }
        }

        // Startup load streams the file through Utf8JsonReader straight into
        // the chunked series.  The previous JsonSerializer path materialized a
        // List<string> with one string per timestamp (series × 86400), which
        // inflated the GC heap at every launch.  Accepted shapes are the same:
        // a root object of series objects with optional name/icon/isTemp,
        // "points" (numbers) and "times" (Unix-second strings, ISO strings, or
        // missing → derived from the file time); unknown properties are
        // skipped and anything else is rejected so recovery can quarantine it.
        static Dictionary<string, Series> ReadSeries(string path, out string json)
        {
            var loaded = new Dictionary<string, Series>();
            var parser = new HistoryParser(path, loaded);
            if (string.Equals(path, FilePath, StringComparison.OrdinalIgnoreCase))
            {
                json = null;
                using (var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 1, FileOptions.SequentialScan))
                    parser.ParseStream(stream);
            }
            else
            {
                // Legacy locations and the .bak fallback are copied to the stable
                // path after loading, so keep their original text for that write.
                byte[] bytes = File.ReadAllBytes(path);
                int bom = bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF ? 3 : 0;
                parser.ParseAll(new ReadOnlySpan<byte>(bytes, bom, bytes.Length - bom));
                json = Encoding.UTF8.GetString(bytes, bom, bytes.Length - bom);
            }
            return loaded;
        }

        // Resumable token-level parser: each token is handled as it arrives,
        // so the input can be fed in fixed-size buffer windows.
        sealed class HistoryParser
        {
            enum Prop { None, Name, Icon, IsTemp, Points, Times, Skip }

            const long NoTime = long.MinValue;
            readonly string _path;
            readonly Dictionary<string, Series> _loaded;
            bool _rootSeen, _rootDone, _rootNull;
            string _key;
            bool _inSeries;
            Prop _prop;
            bool _inPoints, _inTimes, _hasTimes;
            int _skipDepth = -1;
            string _name, _icon;
            bool _isTemp;
            float[] _points = new float[4096];
            long[] _times = new long[4096];
            int _pointCount, _timeCount;
            long? _fileUnix;

            public HistoryParser(string path, Dictionary<string, Series> loaded)
            {
                _path = path;
                _loaded = loaded;
            }

            public void ParseAll(ReadOnlySpan<byte> utf8)
            {
                var reader = new Utf8JsonReader(utf8, isFinalBlock: true, state: default);
                while (reader.Read()) Handle(ref reader);
                Finish();
            }

            public void ParseStream(Stream stream)
            {
                byte[] buffer = ArrayPool<byte>.Shared.Rent(64 * 1024);
                try
                {
                    var state = new JsonReaderState();
                    int filled = 0;
                    bool final = false;
                    bool first = true;
                    while (true)
                    {
                        if (!final)
                        {
                            int read = stream.Read(buffer, filled, buffer.Length - filled);
                            if (read == 0) final = true;
                            filled += read;
                        }
                        int start = 0;
                        if (first && filled >= 3 && buffer[0] == 0xEF && buffer[1] == 0xBB && buffer[2] == 0xBF) start = 3;
                        if (first && (filled >= 3 || final)) first = false;
                        else if (first) continue;

                        var reader = new Utf8JsonReader(new ReadOnlySpan<byte>(buffer, start, filled - start), final, state);
                        while (reader.Read()) Handle(ref reader);
                        state = reader.CurrentState;
                        int consumed = start + (int)reader.BytesConsumed;
                        if (final) break;
                        int leftover = filled - consumed;
                        if (consumed == 0 && filled == buffer.Length)
                        {
                            // A single token is larger than the window: grow it.
                            byte[] larger = ArrayPool<byte>.Shared.Rent(buffer.Length * 2);
                            Buffer.BlockCopy(buffer, 0, larger, 0, filled);
                            ArrayPool<byte>.Shared.Return(buffer);
                            buffer = larger;
                        }
                        else if (leftover > 0 && consumed > 0)
                        {
                            Buffer.BlockCopy(buffer, consumed, buffer, 0, leftover);
                            filled = leftover;
                        }
                        else if (consumed > 0)
                        {
                            filled = 0;
                        }
                    }
                }
                finally
                {
                    ArrayPool<byte>.Shared.Return(buffer);
                }
                Finish();
            }

            void Finish()
            {
                if (!_rootSeen) throw new JsonException("History file is empty.");
                if (!_rootDone && !_rootNull) throw new JsonException("History file ended early.");
            }

            void Handle(ref Utf8JsonReader reader)
            {
                JsonTokenType token = reader.TokenType;
                if (_skipDepth >= 0)
                {
                    if ((token == JsonTokenType.EndObject || token == JsonTokenType.EndArray) && reader.CurrentDepth == _skipDepth)
                        _skipDepth = -1;
                    return;
                }
                if (!_rootSeen)
                {
                    _rootSeen = true;
                    if (token == JsonTokenType.Null) { _rootNull = true; return; }
                    if (token != JsonTokenType.StartObject) throw new JsonException("History root must be an object.");
                    return;
                }
                if (_inPoints)
                {
                    if (token == JsonTokenType.EndArray) { _inPoints = false; return; }
                    if (token != JsonTokenType.Number) throw new JsonException("History points must be numbers.");
                    if (_pointCount == _points.Length) Array.Resize(ref _points, _points.Length * 2);
                    _points[_pointCount++] = reader.GetSingle();
                    return;
                }
                if (_inTimes)
                {
                    if (token == JsonTokenType.EndArray) { _inTimes = false; return; }
                    long value;
                    if (token == JsonTokenType.String) value = ParseTime(ref reader);
                    else if (token == JsonTokenType.Null) value = NoTime;
                    else if (token == JsonTokenType.Number) value = reader.TryGetInt64(out long n) ? n : NoTime;
                    else throw new JsonException("History times must be strings.");
                    if (_timeCount == _times.Length) Array.Resize(ref _times, _times.Length * 2);
                    _times[_timeCount++] = value;
                    return;
                }
                if (!_inSeries)
                {
                    // Depth 1: series key, its object, or the end of the root.
                    if (token == JsonTokenType.EndObject) { _rootDone = true; return; }
                    if (token == JsonTokenType.PropertyName) { _key = reader.GetString(); return; }
                    if (token == JsonTokenType.Null) throw new InvalidDataException("Series data is missing for " + _key);
                    if (token != JsonTokenType.StartObject) throw new JsonException("History series must be an object.");
                    BeginSeries();
                    return;
                }
                // Depth 2: properties of one series.
                if (token == JsonTokenType.EndObject) { EndSeries(); return; }
                if (token == JsonTokenType.PropertyName)
                {
                    _prop = reader.ValueTextEquals("name") ? Prop.Name
                        : reader.ValueTextEquals("icon") ? Prop.Icon
                        : reader.ValueTextEquals("isTemp") ? Prop.IsTemp
                        : reader.ValueTextEquals("points") ? Prop.Points
                        : reader.ValueTextEquals("times") ? Prop.Times
                        : Prop.Skip;
                    return;
                }
                switch (_prop)
                {
                    case Prop.Name:
                    case Prop.Icon:
                        if (token != JsonTokenType.String && token != JsonTokenType.Null) throw new JsonException("History name/icon must be strings.");
                        string text = token == JsonTokenType.Null ? null : reader.GetString();
                        if (_prop == Prop.Name) _name = text; else _icon = text;
                        break;
                    case Prop.IsTemp:
                        if (token == JsonTokenType.True) _isTemp = true;
                        else if (token == JsonTokenType.False) _isTemp = false;
                        else throw new JsonException("History isTemp must be a boolean.");
                        break;
                    case Prop.Points:
                        _pointCount = 0;
                        if (token == JsonTokenType.StartArray) _inPoints = true;
                        else if (token != JsonTokenType.Null) throw new JsonException("History points must be an array.");
                        break;
                    case Prop.Times:
                        _timeCount = 0;
                        _hasTimes = token == JsonTokenType.StartArray;
                        if (_hasTimes) _inTimes = true;
                        else if (token != JsonTokenType.Null) throw new JsonException("History times must be an array.");
                        break;
                    default:
                        if (token == JsonTokenType.StartObject || token == JsonTokenType.StartArray) _skipDepth = reader.CurrentDepth;
                        break;
                }
                _prop = Prop.None;
            }

            void BeginSeries()
            {
                _inSeries = true;
                _name = null; _icon = null; _isTemp = false;
                _pointCount = 0; _timeCount = 0; _hasTimes = false;
                _prop = Prop.None;
            }

            void EndSeries()
            {
                _inSeries = false;
                var s = new Series { Name = _name, Icon = _icon, IsTemp = _isTemp };
                int count = _pointCount;
                int first = Math.Max(0, count - Capacity);
                for (int i = first; i < count; i++)
                {
                    long unix = _hasTimes && i < _timeCount ? _times[i] : NoTime;
                    if (unix == NoTime) unix = FileUnix() - (count - 1 - i);
                    s.Append(_points[i], unix);
                }
                _loaded[_key ?? ""] = s;
            }

            long FileUnix()
            {
                if (_fileUnix == null) _fileUnix = new DateTimeOffset(File.GetLastWriteTimeUtc(_path)).ToUnixTimeSeconds();
                return _fileUnix.Value;
            }

            // Same rules as the former string path: invariant Unix seconds
            // first, then a round-trip date string; anything else (or an
            // out-of-range value) falls back to the file-time estimate.
            static long ParseTime(ref Utf8JsonReader reader)
            {
                if (!reader.ValueIsEscaped
                    && Utf8Parser.TryParse(reader.ValueSpan, out long fast, out int consumed)
                    && consumed == reader.ValueSpan.Length)
                    return InUnixRange(fast) ? fast : NoTime;
                string raw = reader.GetString();
                if (long.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out long unixSec))
                    return InUnixRange(unixSec) ? unixSec : NoTime;
                if (DateTime.TryParse(raw, null, DateTimeStyles.RoundtripKind, out DateTime parsed))
                {
                    try { return ToUnixSeconds(parsed); }
                    catch (ArgumentOutOfRangeException) { return NoTime; }
                }
                return NoTime;
            }

            static bool InUnixRange(long seconds) => seconds >= -62135596800L && seconds <= 253402300799L;
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
