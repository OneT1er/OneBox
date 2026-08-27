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

    /// <summary>
    /// 前台应用切换历史：独立轻量定时器（2s）用 CaptureExeName 检测前台 exe 变化，记录切换点。
    /// 供大图 tooltip 显示“鼠标时间点对应的前台应用”。仅性能趋势图打开时启动（引用计数）。
    /// 退出写 JSON / 启动读 JSON，跨重启保留历史。只调 GetForegroundWindow+QueryFullProcessImageName，开销小。
    /// </summary>
    public static class ForegroundHistory
    {
        struct Entry { public DateTime Time; public string Exe; }
        static readonly object _lock = new object();
        static readonly List<Entry> _entries = new List<Entry>();
        static string _lastExe;
        static Timer _timer;
        static bool _running;
        // 引用计数：仅当性能趋势图窗口打开时才采集 + 驻留内存（与 PerfHistory 一致）。
        static int _openCount;
        static bool _loaded;   // 已从磁盘加载；Save 未加载时跳过，避免空写覆盖
        const int MaxEntries = 3000;

        static string _fpath;
        static string FilePath
        {
            get
            {
                if (_fpath == null)
                {
                    var exe = Environment.ProcessPath;
                    string dir = string.IsNullOrEmpty(exe) ? AppDomain.CurrentDomain.BaseDirectory : Path.GetDirectoryName(exe);
                    _fpath = Path.Combine(dir, "OneBox.foreground.json");
                }
                return _fpath;
            }
        }

        public static void Start()
        {
            lock (_lock)
            {
                if (_running) return;
                _running = true;
                _timer = new Timer(Tick, null, 1000, 2000);
            }
            AppLog.Log("FGHistory", "started");
        }

        public static void Stop()
        {
            lock (_lock) { _running = false; _timer?.Dispose(); _timer = null; }
        }

        // 性能趋势图窗口打开/关闭时调用（引用计数）。首次打开 Load+Start；最后关闭 Stop+Save+Clear 释放。
        public static void Acquire()
        {
            lock (_lock)
            {
                if (_openCount == 0) { _loaded = Load(); Start(); }
                _openCount++;
            }
        }

        public static void Release()
        {
            lock (_lock)
            {
                if (_openCount == 0) return;
                if (--_openCount == 0)
                {
                    Stop();
                    try { Save(); } catch (Exception ex) { AppLog.Log("FGHistory", ex); }
                    _entries.Clear(); _lastExe = null; _loaded = false;
                    AppLog.Log("FGHistory", "released (in-memory cleared)");
                }
            }
        }

        static void Tick(object state)
        {
            if (!_running) return;
            try
            {
                string exe = ForegroundWatcher.CaptureExeName() ?? "";
                lock (_lock)
                {
                    if (!string.Equals(exe, _lastExe, StringComparison.OrdinalIgnoreCase))
                    {
                        _lastExe = exe;
                        _entries.Add(new Entry { Time = DateTime.Now, Exe = exe });
                        if (_entries.Count > MaxEntries) _entries.RemoveAt(0);
                    }
                }
            }
            catch { }
        }

        // 取 [from, to] 范围内的前台段：每段从一个切换点到下一个切换点，exe 为该段前台
        public static List<ForegroundSegment> GetSegments(DateTime from, DateTime to)
        {
            var result = new List<ForegroundSegment>();
            lock (_lock)
            {
                if (_entries.Count == 0) return result;
                if (_entries[0].Time > from)
                    result.Add(new ForegroundSegment { Start = from, End = _entries[0].Time, Exe = _entries[0].Exe });

                for (int i = 0; i < _entries.Count; i++)
                {
                    var e = _entries[i];
                    DateTime segStart = e.Time;
                    DateTime segEnd = (i + 1 < _entries.Count) ? _entries[i + 1].Time : to;
                    if (segEnd <= from) continue;
                    if (segStart >= to) break;
                    if (segStart < from) segStart = from;
                    if (segEnd > to) segEnd = to;
                    if (segEnd > segStart)
                        result.Add(new ForegroundSegment { Start = segStart, End = segEnd, Exe = e.Exe });
                }
            }
            return result;
        }

        public static void Clear() { lock (_lock) { _entries.Clear(); _lastExe = null; } }

        // ---- 持久化 ----
        class EntryData { public string time { get; set; } public string exe { get; set; } }

        public static void Save()
        {
            try
            {
                if (!_loaded) return;   // 未加载（图表已关闭，Release 时已存盘）：不空写覆盖
                List<EntryData> data;
                lock (_lock) data = _entries.Select(e => new EntryData { time = e.Time.ToString("o"), exe = e.Exe }).ToList();
                File.WriteAllText(FilePath, JsonSerializer.Serialize(data));
                AppLog.Log("FGHistory", "saved " + data.Count);
            }
            catch (Exception ex) { AppLog.Log("FGHistory", "save fail: " + ex.Message); }
        }

        // 返回是否成功加载（文件不存在视为成功）。失败时 Release 的 Save 跳过，避免空写覆盖旧文件。
        public static bool Load()
        {
            try
            {
                if (!File.Exists(FilePath)) return true;
                var data = JsonSerializer.Deserialize<List<EntryData>>(File.ReadAllText(FilePath));
                if (data == null) return true;
                lock (_lock)
                {
                    _entries.Clear();
                    string ownExe = Path.GetFileNameWithoutExtension(Environment.ProcessPath ?? "OneBox");
                    foreach (var d in data)
                        if (DateTime.TryParse(d.time, null, System.Globalization.DateTimeStyles.RoundtripKind, out var t))
                        {
                            // 旧版本会在趋势窗口取得焦点后持续写入 OneBox。加载时清掉这些
                            // 无效记录，避免修复升级后的首次打开仍被旧历史铺满。
                            if (string.Equals(d.exe, ownExe, StringComparison.OrdinalIgnoreCase)
                                || string.Equals(d.exe, "OneBox", StringComparison.OrdinalIgnoreCase)) continue;
                            _entries.Add(new Entry { Time = t, Exe = d.exe });
                        }
                    _lastExe = _entries.Count > 0 ? _entries[_entries.Count - 1].Exe : null;
                }
                AppLog.Log("FGHistory", "loaded " + _entries.Count);
                return true;
            }
            catch (Exception ex) { AppLog.Log("FGHistory", "load fail: " + ex.Message); return false; }
        }
    }
}
