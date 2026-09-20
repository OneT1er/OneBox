using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;

namespace PowerAudioManager
{
    public class ForegroundSegment
    {
        public DateTime Start;
        public DateTime End;
        public string Exe;
    }

    // 每 2 秒采样实际前台；区间有明确结束时间，不向未采集时段外推。
    public static class ForegroundHistory
    {
        struct Entry { public DateTime Time, End; public string Exe; }
        static readonly object _lock = new object();
        static readonly object _saveLock = new object();
        static readonly List<Entry> _entries = new List<Entry>();
        static Timer _timer;
        static bool _running, _loaded, _newSession = true;
        static int _generation;
        static DateTime _lastSave;
        const int MaxEntries = 43200;
        static string FilePath => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "OneT1er", "OneBox", "OneBox.foreground.json");

        public static void Start()
        {
            lock (_lock)
            {
                if (_running) return;
                if (!_loaded) _loaded = Load();
                _newSession = true;
                _running = true;
                _lastSave = DateTime.UtcNow;
                _timer = new Timer(Tick, ++_generation, 0, 2000);
            }
        }

        public static void Stop()
        {
            lock (_lock)
            {
                _running = false;
                ++_generation;
                _timer?.Dispose(); _timer = null;
                if (_entries.Count > 0)
                {
                    var last = _entries[^1];
                    if (last.End > DateTime.Now) { last.End = DateTime.Now; _entries[^1] = last; }
                }
                _newSession = true;
            }
            Save();
        }

        // 图表只读取历史，采集生命周期由性能监控模块管理。
        public static void Acquire() { lock (_lock) { if (!_loaded) _loaded = Load(); } }
        public static void Release() { }

        static void Tick(object state)
        {
            bool save;
            lock (_lock)
            {
                if (!_running || (int)state != _generation) return;
                RecordSample(DateTime.Now, ForegroundWatcher.CaptureActualExeName());
                save = (DateTime.UtcNow - _lastSave).TotalSeconds >= 60;
                if (save) _lastSave = DateTime.UtcNow;
            }
            if (save) Save();
        }

        internal static void RecordSample(DateTime time, string exe)
        {
            lock (_lock)
            {
                if (_entries.Count > 0)
                {
                    var last = _entries[^1];
                    // 时钟回拨时丢弃重叠的未来记录，保持区间有序。
                    if (time < last.Time)
                    {
                        _entries.RemoveAll(e => e.Time >= time);
                        _newSession = true;
                    }
                    else if (!_newSession && time <= last.End.AddSeconds(2) &&
                             string.Equals(last.Exe, exe ?? "", StringComparison.OrdinalIgnoreCase))
                    {
                        last.End = time.AddSeconds(2);
                        _entries[^1] = last;
                        return;
                    }
                    if (_entries.Count > 0 && _entries[^1].End > time)
                    {
                        last = _entries[^1]; last.End = time; _entries[^1] = last;
                    }
                }
                _entries.Add(new Entry { Time = time, End = time.AddSeconds(2), Exe = exe ?? "" });
                _newSession = false;
                _entries.RemoveAll(e => e.End < time.AddDays(-1));
                if (_entries.Count > MaxEntries) _entries.RemoveRange(0, _entries.Count - MaxEntries);
            }
        }

        public static List<ForegroundSegment> GetSegments(DateTime from, DateTime to)
        {
            lock (_lock)
            {
                return _entries.Where(e => e.End > from && e.Time < to && !string.IsNullOrEmpty(e.Exe))
                    .Select(e => new ForegroundSegment {
                        Start = e.Time < from ? from : e.Time,
                        End = e.End > to ? to : e.End, Exe = e.Exe
                    }).Where(e => e.End > e.Start).ToList();
            }
        }

        public static void Clear() { lock (_lock) { _entries.Clear(); _newSession = true; } }
        class EntryData { public DateTime time { get; set; } public DateTime? end { get; set; } public string exe { get; set; } }

        public static void Save()
        {
            lock (_saveLock)
            {
                try
                {
                    List<EntryData> data;
                    lock (_lock)
                    {
                        if (!_loaded) return;
                        data = _entries.Select(e => new EntryData { time = e.Time, end = e.End, exe = e.Exe }).ToList();
                    }
                    DurableFileStore.WriteUtf8Atomically(FilePath, JsonSerializer.Serialize(data));
                }
                catch (Exception ex) { AppLog.Log("FGHistory", ex); }
            }
        }

        public static bool Load()
        {
            lock (_lock)
            {
                foreach (var path in new[] { FilePath, FilePath + ".bak" })
                {
                    if (!File.Exists(path)) continue;
                    try
                    {
                        var data = JsonSerializer.Deserialize<List<EntryData>>(File.ReadAllText(path));
                        _entries.Clear();
                        // 旧格式没有结束时间，无法证明覆盖范围，不能当作连续历史。
                        foreach (var d in (data ?? new List<EntryData>()).OrderBy(d => d.time).TakeLast(MaxEntries))
                        {
                            if (!d.end.HasValue || d.end.Value <= d.time) continue;
                            _entries.Add(new Entry { Time = d.time, End = d.end.Value, Exe = d.exe ?? "" });
                        }
                        _newSession = true;
                        return true;
                    }
                    catch (Exception ex) { AppLog.Log("FGHistory", ex); }
                }
                return !File.Exists(FilePath) && !File.Exists(FilePath + ".bak");
            }
        }
    }
}
