using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;

namespace PowerAudioManager
{
    /// <summary>
    /// 阶段2：训练样本持久化。把「特征快照 + 用户选择的电源/音频」追加保存到 exe 同目录 OneBox.samples.csv。
    /// 手动切换电源计划或音频设备时，由 LearningEngine 调 Append 记一条；训练时 LoadAll 读回全部样本。
    /// CSV 列：time,cpu,gpu,fullscreen,battery,hour,category,exe,power,audio
    /// </summary>
    public static class SampleStore
    {
        public class Sample
        {
            public DateTime Time;
            public float Cpu;            // 0-100
            public float Gpu;            // 0-100，-1=不可用
            public float Fullscreen;     // 0/1
            public float Battery;        // 0/1（1=电池）
            public float Hour;           // 0-24
            public string Category;      // Other/Game/Creative/VideoConf
            public string Exe;
            public string PowerPlan;     // GUID
            public string AudioDevice;   // 端点 Id
        }

        static readonly object _lock = new object();
        static string _path;
        static int _count = -1;   // 缓存样本数；-1=未初始化（首次访问时读文件）。Append 自增、Clear 清零，避免每秒读整个 CSV。
        static string FilePath
        {
            get
            {
                if (_path == null)
                {
                    var exe = Environment.ProcessPath;
                    string dir = string.IsNullOrEmpty(exe) ? AppDomain.CurrentDomain.BaseDirectory : Path.GetDirectoryName(exe);
                    _path = Path.Combine(dir, "OneBox.samples.csv");
                }
                return _path;
            }
        }

        public static int Count
        {
            get
            {
                if (_count >= 0) return _count;
                lock (_lock)
                {
                    if (_count >= 0) return _count;
                    _count = CountFile();
                    return _count;
                }
            }
        }

        // 实际读文件计非空行数（减去表头）。仅用于缓存初始化，不在热路径调用。
        static int CountFile()
        {
            try
            {
                if (!File.Exists(FilePath)) return 0;
                int n = 0; bool first = true;
                foreach (var line in File.ReadLines(FilePath))
                {
                    if (first) { first = false; continue; }
                    if (!string.IsNullOrWhiteSpace(line)) n++;
                }
                return n;
            }
            catch { return 0; }
        }

        public static void Append(FeatureCollector.Snapshot s, string chosenPower, string chosenAudio)
        {
            if (s == null) return;
            if (string.IsNullOrEmpty(chosenPower) && string.IsNullOrEmpty(chosenAudio)) return;
            lock (_lock)
            {
                try
                {
                    bool writeHeader = !File.Exists(FilePath);
                    using (var fs = new FileStream(FilePath, FileMode.Append, FileAccess.Write, FileShare.Read))
                    using (var sw = new StreamWriter(fs, new UTF8Encoding(false)))
                    {
                        if (writeHeader)
                            sw.WriteLine("time,cpu,gpu,fullscreen,battery,hour,category,exe,power,audio");
                        sw.WriteLine(string.Join(",",
                            Csv(s.Time.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture)),
                            s.CpuLoad.ToString("0.0", CultureInfo.InvariantCulture),
                            s.GpuLoad.ToString("0.0", CultureInfo.InvariantCulture),
                            s.Fullscreen ? "1" : "0",
                            s.OnBattery ? "1" : "0",
                            s.Hour.ToString("0.00", CultureInfo.InvariantCulture),
                            Csv(s.CategoryName),
                            Csv(s.ExeName ?? ""),
                            Csv(chosenPower ?? ""),
                            Csv(chosenAudio ?? "")));
                    }
                    _count = _count < 0 ? CountFile() : _count + 1;   // 写入成功才更新缓存
                }
                catch (Exception ex) { AppLog.Log("Sample", "append fail: " + ex.Message); }
            }
        }

        public static List<Sample> LoadAll()
        {
            var list = new List<Sample>();
            try
            {
                if (!File.Exists(FilePath)) return list;
                bool first = true;
                foreach (var line in File.ReadLines(FilePath))
                {
                    if (first) { first = false; continue; }
                    if (string.IsNullOrWhiteSpace(line)) continue;
                    var s = Parse(line);
                    if (s != null) list.Add(s);
                }
            }
            catch (Exception ex) { AppLog.Log("Sample", "load fail: " + ex.Message); }
            return list;
        }

        public static void Clear()
        {
            lock (_lock)
            {
                _count = 0;
                try { if (File.Exists(FilePath)) File.Delete(FilePath); } catch (Exception ex) { AppLog.Log("Sample", "clear fail: " + ex.Message); }
            }
            AppLog.Log("Sample", "cleared");
        }

        static Sample Parse(string line)
        {
            try
            {
                var f = SplitCsv(line);
                if (f.Length < 10) return null;
                return new Sample
                {
                    Time = DateTime.TryParse(f[0], CultureInfo.InvariantCulture, DateTimeStyles.AssumeLocal, out var t) ? t : DateTime.Now,
                    Cpu = F(f[1]),
                    Gpu = F(f[2]),
                    Fullscreen = F(f[3]),
                    Battery = F(f[4]),
                    Hour = F(f[5]),
                    Category = string.IsNullOrEmpty(f[6]) ? "Other" : f[6],
                    Exe = f[7] ?? "",
                    PowerPlan = f[8] ?? "",
                    AudioDevice = f[9] ?? "",
                };
            }
            catch { return null; }
        }

        static float F(string s) => float.TryParse(s?.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out var v) ? v : 0;

        // 极简 CSV：含逗号/引号/换行的字段加引号并转义双引号。
        static string Csv(string s)
        {
            if (s == null) return "";
            if (s.IndexOfAny(new[] { ',', '"', '\n', '\r' }) < 0) return s;
            return "\"" + s.Replace("\"", "\"\"") + "\"";
        }

        static string[] SplitCsv(string line)
        {
            var res = new List<string>();
            var sb = new StringBuilder();
            bool inQ = false;
            for (int i = 0; i < line.Length; i++)
            {
                char c = line[i];
                if (inQ)
                {
                    if (c == '"')
                    {
                        if (i + 1 < line.Length && line[i + 1] == '"') { sb.Append('"'); i++; }
                        else inQ = false;
                    }
                    else sb.Append(c);
                }
                else
                {
                    if (c == '"') inQ = true;
                    else if (c == ',') { res.Add(sb.ToString()); sb.Clear(); }
                    else sb.Append(c);
                }
            }
            res.Add(sb.ToString());
            return res.ToArray();
        }
    }
}
