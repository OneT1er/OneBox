using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace PowerAudioManager
{
    /// <summary>
    /// 自学习（投票统计版）：为每个前台应用累积观察电源计划/音频输出的出现次数，
    /// 套用时取票数最多的组合。偶发手动改只投一票，不会立即覆盖已稳定的习惯；多次后收敛。
    /// 规则持久化到 exe 同目录 OneBox.profiles.json。总开关 Learn.Enabled，通知开关 Learn.Notify。
    /// 防循环：自动套用后 6 秒内不投票。Locked 规则不自动学习（用户手动编辑后锁定）。
    /// </summary>
    public static class AppProfileService
    {
        public class Rule
        {
            public string ExeName { get; set; }
            public Dictionary<string, int> PowerPlanVotes { get; set; } = new Dictionary<string, int>();
            public Dictionary<string, int> AudioVotes { get; set; } = new Dictionary<string, int>();
            public bool Disabled { get; set; }      // per-app 禁用自动套用
            public bool Locked { get; set; }         // 锁定：不自动投票，套用设定值（用户编辑后）
            public DateTime UpdatedAt { get; set; }

            [JsonIgnore] public string PowerPlanGuid => TopKey(PowerPlanVotes);
            [JsonIgnore] public string AudioDeviceId => TopKey(AudioVotes);
            [JsonIgnore] public int TopPowerVotes => PowerPlanVotes.Count > 0 ? PowerPlanVotes.Values.Max() : 0;
            [JsonIgnore] public int TopAudioVotes => AudioVotes.Count > 0 ? AudioVotes.Values.Max() : 0;

            static string TopKey(Dictionary<string, int> d)
            {
                if (d == null || d.Count == 0) return "";
                int max = -1; string k = "";
                foreach (var kv in d) if (kv.Value > max) { max = kv.Value; k = kv.Key; }
                return k;
            }
        }

        static readonly object _lock = new object();
        static Dictionary<string, Rule> _rules = new Dictionary<string, Rule>(StringComparer.OrdinalIgnoreCase);
        static string _profilePath;
        static DateTime _autoApplyingUntil;
        static bool _started;

        static string ProfilePath
        {
            get
            {
                if (_profilePath == null)
                {
                    var exe = Environment.ProcessPath;
                    string dir = string.IsNullOrEmpty(exe) ? AppDomain.CurrentDomain.BaseDirectory : Path.GetDirectoryName(exe);
                    _profilePath = Path.Combine(dir, "OneBox.profiles.json");
                }
                return _profilePath;
            }
        }

        public static bool IsStarted => _started;
        public static int RuleCount { get { lock (_lock) return _rules.Count; } }

        public static void Start()
        {
            if (_started) return;
            _started = true;
            Load();
            ForegroundWatcher.ForegroundChanged += OnForegroundChanged;
            ForegroundWatcher.StateChanged += OnStateChanged;
            AppLog.Log("Profile", $"started: rules={_rules.Count} path={ProfilePath}");
        }

        public static void Stop()
        {
            if (!_started) return;
            _started = false;
            ForegroundWatcher.ForegroundChanged -= OnForegroundChanged;
            ForegroundWatcher.StateChanged -= OnStateChanged;
            AppLog.Log("Profile", "stopped");
        }

        static bool Enabled => AppPrefs.GetBool("Learn.Enabled", false);

        // 前台 exe 切换：有规则则套用票数最多，无规则则首次投票。
        static void OnForegroundChanged(ForegroundWatcher.Snapshot s)
        {
            if (!Enabled) return;
            string exe = s.ExeName;
            if (string.IsNullOrEmpty(exe)) return;

            Rule r;
            bool isNew;
            lock (_lock)
            {
                isNew = !_rules.TryGetValue(exe, out r);
                if (isNew)
                {
                    r = new Rule { ExeName = exe, UpdatedAt = DateTime.UtcNow };
                    _rules[exe] = r;
                    Vote(r, s.PowerPlanGuid, s.AudioDeviceId);
                    SaveLocked();
                }
            }
            if (isNew)
            {
                AppLog.Log("Profile", $"learn(new): {exe} power={s.PowerPlanGuid} audio={s.AudioDeviceId}");
                return;
            }
            if (r.Disabled) return;
            ApplyRule(r, s);
        }

        // 电源/音频变化：用户手动改 -> 给当前配置投一票（累积，不覆盖）。
        static void OnStateChanged(ForegroundWatcher.Snapshot s)
        {
            if (!Enabled) return;
            if (DateTime.UtcNow < _autoApplyingUntil) return;   // 防循环：自动套用窗口内不投票
            string exe = s.ExeName;
            if (string.IsNullOrEmpty(exe)) return;

            lock (_lock)
            {
                if (!_rules.TryGetValue(exe, out var r))
                {
                    r = new Rule { ExeName = exe, UpdatedAt = DateTime.UtcNow };
                    _rules[exe] = r;
                    Vote(r, s.PowerPlanGuid, s.AudioDeviceId);
                    SaveLocked();
                    return;
                }
                if (r.Disabled || r.Locked) return;
                Vote(r, s.PowerPlanGuid, s.AudioDeviceId, 10);  // 手动切换权重高，避免被旧累积票数覆盖
                SaveLocked();
                AppLog.Log("Profile", $"vote: {exe} power={s.PowerPlanGuid}(top={r.PowerPlanGuid}/{r.TopPowerVotes}) audio={s.AudioDeviceId}(top={r.AudioDeviceId}/{r.TopAudioVotes})");
            }
        }

        static void Vote(Rule r, string power, string audio, int weight = 1)
        {
            if (!string.IsNullOrEmpty(power))
            {
                if (!r.PowerPlanVotes.ContainsKey(power)) r.PowerPlanVotes[power] = 0;
                r.PowerPlanVotes[power] += weight;
            }
            if (!string.IsNullOrEmpty(audio))
            {
                if (!r.AudioVotes.ContainsKey(audio)) r.AudioVotes[audio] = 0;
                r.AudioVotes[audio] += weight;
            }
            r.UpdatedAt = DateTime.UtcNow;
        }

        static void ApplyRule(Rule r, ForegroundWatcher.Snapshot s)
        {
            string power = r.PowerPlanGuid;
            string audio = r.AudioDeviceId;
            bool needPower = !string.IsNullOrEmpty(power) && !string.Equals(power, s.PowerPlanGuid, StringComparison.OrdinalIgnoreCase);
            bool needAudio = !string.IsNullOrEmpty(audio) && !string.Equals(audio, s.AudioDeviceId, StringComparison.OrdinalIgnoreCase);
            if (!needPower && !needAudio) return;

            _autoApplyingUntil = DateTime.UtcNow.AddSeconds(6);
            AppLog.Log("Profile", $"apply: {r.ExeName} power={(needPower ? power : "-")} audio={(needAudio ? audio : "-")}");
            try
            {
                if (needPower) PowerPlanService.SetActivePlan(power);
                if (needAudio) AudioDevices.SetDefaultDevice(audio);
            }
            catch (Exception ex) { AppLog.Log("Profile", "apply fail: " + ex.Message); }

            if (AppPrefs.GetBool("Learn.Notify", true))
            {
                AppProfileToast.Show(r.ExeName,
                    needPower ? FriendlyPowerName(power) : null,
                    needAudio ? FriendlyAudioName(audio) : null);
            }
        }

        // ---- 设置面板查询/编辑 ----
        public static List<Rule> GetAllRules() { lock (_lock) return _rules.Values.OrderByDescending(r => r.UpdatedAt).ToList(); }
        public static Rule GetRule(string exe) { lock (_lock) return _rules.TryGetValue(exe, out var r) ? r : null; }

        public static void DeleteRule(string exe) { lock (_lock) { _rules.Remove(exe); SaveLocked(); } AppLog.Log("Profile", "delete: " + exe); }
        public static void SetDisabled(string exe, bool disabled) { lock (_lock) { if (_rules.TryGetValue(exe, out var r)) { r.Disabled = disabled; SaveLocked(); } } }
        public static void Unlock(string exe) { lock (_lock) { if (_rules.TryGetValue(exe, out var r)) { r.Locked = false; SaveLocked(); } } }
        public static void ClearAll() { lock (_lock) { _rules.Clear(); SaveLocked(); } AppLog.Log("Profile", "cleared all"); }

        // 手动编辑规则：清空投票只留设定值，并锁定（不再被自动学习覆盖）。Unlock 可恢复学习。
        public static void SetRule(string exe, string powerGuid, string audioId)
        {
            lock (_lock)
            {
                if (!_rules.TryGetValue(exe, out var r)) { r = new Rule { ExeName = exe }; _rules[exe] = r; }
                r.PowerPlanVotes.Clear();
                r.AudioVotes.Clear();
                if (!string.IsNullOrEmpty(powerGuid)) r.PowerPlanVotes[powerGuid] = 1;
                if (!string.IsNullOrEmpty(audioId)) r.AudioVotes[audioId] = 1;
                r.Locked = true;
                r.Disabled = false;
                r.UpdatedAt = DateTime.UtcNow;
                SaveLocked();
            }
            AppLog.Log("Profile", $"set(locked): {exe} power={powerGuid} audio={audioId}");
        }

        // 友好名（Toast + 设置面板共用）
        public static string FriendlyPowerName(string guid)
        {
            if (string.IsNullOrEmpty(guid)) return "电源(未设)";
            try { var p = PowerPlanService.GetPowerPlans().Find(x => x.Guid.Equals(guid, StringComparison.OrdinalIgnoreCase)); return p != null ? p.Name : guid; }
            catch { return guid; }
        }
        public static string FriendlyAudioName(string id)
        {
            if (string.IsNullOrEmpty(id)) return "音频(未设)";
            try { var d = AudioDevices.GetOutputDevices().Find(x => string.Equals(x.Id, id, StringComparison.OrdinalIgnoreCase)); return d != null ? d.Name : id; }
            catch { return id; }
        }

        // ---- 持久化 ----
        static void Load()
        {
            try
            {
                if (!File.Exists(ProfilePath)) return;
                var json = File.ReadAllText(ProfilePath);
                var d = JsonSerializer.Deserialize<Dictionary<string, Rule>>(json);
                if (d != null) _rules = new Dictionary<string, Rule>(d, StringComparer.OrdinalIgnoreCase);
            }
            catch (Exception ex) { AppLog.Log("Profile", "load fail: " + ex.Message); }
        }

        static void SaveLocked()   // 调用者持 _lock
        {
            try
            {
                var opts = new JsonSerializerOptions { WriteIndented = true };
                File.WriteAllText(ProfilePath, JsonSerializer.Serialize(_rules, opts));
            }
            catch (Exception ex) { AppLog.Log("Profile", "save fail: " + ex.Message); }
        }
    }
}
