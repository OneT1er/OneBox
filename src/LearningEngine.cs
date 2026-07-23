using System;
using System.Threading;

namespace PowerAudioManager
{
    /// <summary>
    /// 阶段4：自学习引擎（替代旧的按应用投票 AppProfileService）。
    /// 1) FeatureCollector 每 1s 采一张特征快照（CPU/GPU/全屏/电池/时间/进程类别/exe），作为推理输入与样本特征。
    /// 2) ForegroundWatcher 每 2s 检测电源/音频变化：变化且不在自动套用宽限期内 => 视为用户手动切换，
    ///    立即用「当前特征快照 + 新选择的电源/音频」记一条样本到 CSV，并暂停自动模式 10 分钟。
    /// 3) 观察式采样：情境稳定时每 45s 把「当前特征 -> 当前电源/音频」也记一条（去重），不依赖手动切换，
    ///    一天正常用机即可积累足够样本，消除旧版"靠手动切 200 次才训练"的慢收集问题。
    /// 4) 若有可用预测器（FastTree 模型 或 ≥20 条样本的 k-NN 回退）且自动套用开启：每秒推理一次，
    ///    预测连续 5s 不变才视为有效；与当前不同则自动切换，切后冷却 30s 防来回跳；手动切换后 10 分钟内不自动套用。
    /// 5) 样本累积达 50 条自动训练 FastTree（每 +25 且距上次≥5min 重训）；30 条可手动训练。k-NN 在 20 条即可兜底，消除冷启动空窗。
    /// 总开关 Learn.Enabled；自动套用开关 Learn.AutoApply（默认开）；通知开关 Learn.Notify。
    /// </summary>
    public static class LearningEngine
    {
        // 防抖/冷却参数
        const int StableSeconds = 5;        // 预测需连续 5s 不变才套用
        const int CooldownSeconds = 30;     // 自动切换后冷却
        const int AutoApplyGraceSeconds = 8;// 自动套用后该窗口内忽略状态变化（避免把自己的切换当手动）
        const int ManualPauseMinutes = 10;  // 手动切换后暂停自动模式
        const int StartGraceSeconds = 4;    // 启动后忽略状态噪声
        const int ObserveIntervalSeconds = 45; // 观察式采样间隔：情境稳定时周期性记一条
        const int MinRetrainMinutes = 5;       // 两次自动训练的最小间隔，避免数据涨起来后频繁重训

        static readonly object _lock = new object();
        static bool _started, _ticking;
        static FeatureCollector.Snapshot _lastFeatures;   // 最新特征（1s 采样）
        static string _currentPower, _currentAudio;       // 当前电源/音频（由 StateChanged 维护）
        static bool _stateInited;                          // 是否已拿到初始电源/音频

        // 推理防抖
        static string _pendingPower, _pendingAudio;
        static int _stableCount;

        // 时间门
        static DateTime _autoPausedUntil;    // 手动切换后暂停自动套用直到
        static DateTime _cooldownUntil;      // 自动切换后冷却直到
        static DateTime _autoApplyGraceUntil;// 自动套用宽限（忽略状态变化）直到
        static DateTime _startAt;

        // 观察式采样
        static DateTime _nextObserveAt;      // 下次可记观察样本的时刻
        static ObservedKey _lastObserved;    // 上一条观察样本（去重参照）
        static bool _hasObserved;

        // 自动训练
        static bool _training;
        static int _lastAutoTrainCount;
        static DateTime _lastTrainAt;

        static bool AutoApply => AppPrefs.GetBool("Learn.AutoApply", true);
        static bool Notify => AppPrefs.GetBool("Learn.Notify", true);

        public static bool IsStarted => _started;

        public static void Start()
        {
            if (_started) return;
            _started = true;
            _startAt = DateTime.UtcNow;
            _stateInited = false;
            _currentPower = null; _currentAudio = null;
            _lastFeatures = null;
            _pendingPower = null; _pendingAudio = null; _stableCount = 0;
            _autoPausedUntil = DateTime.MinValue;
            _cooldownUntil = DateTime.MinValue;
            _autoApplyGraceUntil = DateTime.MinValue;
            _nextObserveAt = _startAt.AddSeconds(StartGraceSeconds + ObserveIntervalSeconds);
            _hasObserved = false;

            FeatureCollector.Sampled += OnSampled;
            ForegroundWatcher.StateChanged += OnStateChanged;
            ForegroundWatcher.ForegroundChanged += OnForegroundChanged;
            FeatureCollector.Start();
            ForegroundWatcher.Start();
            DecisionTreeLearner.Load();   // 后台加载已有模型

            var meta = DecisionTreeLearner.LoadMeta();
            _lastAutoTrainCount = meta?.SampleCount ?? 0;
            AppLog.Log("Learn", $"engine started autoApply={AutoApply}");
        }

        public static void Stop()
        {
            if (!_started) return;
            _started = false;
            FeatureCollector.Sampled -= OnSampled;
            ForegroundWatcher.StateChanged -= OnStateChanged;
            ForegroundWatcher.ForegroundChanged -= OnForegroundChanged;
            FeatureCollector.Stop();
            ForegroundWatcher.Stop();
            AppLog.Log("Learn", "engine stopped");
        }

        // ---- 1s 特征采样：更新最新特征 + 观察式记样本 + 跑推理 ----
        static void OnSampled(FeatureCollector.Snapshot s)
        {
            if (!_started) return;
            _lastFeatures = s;
            MaybeObserve(s);
            if (AutoApply) TryInfer(s);
        }

        // 观察式采样：情境稳定时周期性把「当前特征 -> 当前电源/音频」记为一条样本。
        // 不依赖手动切换，显著加快样本积累（一天正常用机即可达自动训练阈值）。去重避免刷屏；
        // 手动切换暂停期内不采（那次切换已作为强信号样本记过，且避免误采过渡态）。
        static void MaybeObserve(FeatureCollector.Snapshot s)
        {
            if (!_stateInited) return;
            var now = DateTime.UtcNow;
            if (now < _startAt.AddSeconds(StartGraceSeconds)) return;
            if (now < _autoPausedUntil) return;
            if (now < _nextObserveAt) return;
            if (string.IsNullOrEmpty(_currentPower) && string.IsNullOrEmpty(_currentAudio)) return;
            _nextObserveAt = now.AddSeconds(ObserveIntervalSeconds);

            // 去重：与上一条观察样本情境一致且负载相近则跳过
            if (_hasObserved && SameObserveContext(_lastObserved, s)) return;

            SampleStore.Append(s, _currentPower, _currentAudio);
            _lastObserved = new ObservedKey(s, _currentPower, _currentAudio);
            _hasObserved = true;
            AppLog.Log("Learn", $"observe sample: exe={s.ExeName} cpu={s.CpuLoad:0} gpu={s.GpuLoad:0} fs={s.Fullscreen} bat={s.OnBattery} h={s.Hour:0.0} cat={s.CategoryName} -> power={_currentPower} audio={_currentAudio}");
            MaybeAutoTrain();
        }

        // 两样本是否属同一稳定情境（exe/类别/全屏/电池/电源/音频全同，且 CPU/GPU 负载在 15% 内）。
        static bool SameObserveContext(in ObservedKey a, FeatureCollector.Snapshot s)
        {
            if (!string.Equals(a.Exe, s.ExeName ?? "", StringComparison.OrdinalIgnoreCase)) return false;
            if (a.Fullscreen != s.Fullscreen) return false;
            if (a.OnBattery != s.OnBattery) return false;
            if (!string.Equals(a.Category, s.CategoryName, StringComparison.OrdinalIgnoreCase)) return false;
            if (!string.Equals(a.Power, _currentPower ?? "", StringComparison.OrdinalIgnoreCase)) return false;
            if (!string.Equals(a.Audio, _currentAudio ?? "", StringComparison.OrdinalIgnoreCase)) return false;
            if (Math.Abs(a.Cpu - s.CpuLoad) > 15) return false;
            float ag = a.Gpu < 0 ? 0 : a.Gpu, sg = s.GpuLoad < 0 ? 0 : s.GpuLoad;
            if (Math.Abs(ag - sg) > 15) return false;
            return true;
        }

        // 观察样本去重键（轻量，只存比较所需字段）。
        struct ObservedKey
        {
            public string Exe; public bool Fullscreen; public bool OnBattery; public string Category;
            public string Power; public string Audio; public float Cpu; public float Gpu;
            public ObservedKey(FeatureCollector.Snapshot s, string power, string audio)
            {
                Exe = s.ExeName ?? ""; Fullscreen = s.Fullscreen; OnBattery = s.OnBattery;
                Category = s.CategoryName; Power = power ?? ""; Audio = audio ?? "";
                Cpu = s.CpuLoad; Gpu = s.GpuLoad;
            }
        }

        // ---- 前台 exe 切换：重置防抖，新情境重新评估 ----
        static void OnForegroundChanged(ForegroundWatcher.Snapshot s)
        {
            _stableCount = 0;
            _pendingPower = null; _pendingAudio = null;
        }

        // ---- 电源/音频变化检测：手动切换 -> 记样本 + 暂停 10 分钟 ----
        static void OnStateChanged(ForegroundWatcher.Snapshot s)
        {
            if (!_started) return;
            var now = DateTime.UtcNow;

            // 维护当前电源/音频（推理比较用）
            bool powerChanged = !string.Equals(_currentPower, s.PowerPlanGuid ?? "", StringComparison.OrdinalIgnoreCase);
            bool audioChanged = !string.Equals(_currentAudio, s.AudioDeviceId ?? "", StringComparison.OrdinalIgnoreCase);
            _currentPower = s.PowerPlanGuid ?? "";
            _currentAudio = s.AudioDeviceId ?? "";

            // 启动噪声 / 自动套用宽限期内的变化（来自我们自己的切换）忽略，不当手动样本。
            if (!_stateInited) { _stateInited = true; return; }
            if (now < _startAt.AddSeconds(StartGraceSeconds)) return;
            if (now < _autoApplyGraceUntil) return;
            if (!powerChanged && !audioChanged) return;

            // 用户手动切换：用当前特征快照 + 新选择记一条样本
            var feat = _lastFeatures;
            if (feat != null)
            {
                SampleStore.Append(feat, s.PowerPlanGuid, s.AudioDeviceId);
                AppLog.Log("Learn", $"manual sample: exe={feat.ExeName} cpu={feat.CpuLoad:0} gpu={feat.GpuLoad:0} fs={feat.Fullscreen} bat={feat.OnBattery} h={feat.Hour:0.0} cat={feat.CategoryName} -> power={s.PowerPlanGuid} audio={s.AudioDeviceId}");
            }
            _autoPausedUntil = now.AddMinutes(ManualPauseMinutes); // 暂停自动模式 10 分钟
            _stableCount = 0;                                      // 重置防抖

            MaybeAutoTrain();
        }

        // ---- 推理 + 防抖 + 冷却 + 自动套用 ----
        static void TryInfer(FeatureCollector.Snapshot s)
        {
            if (_ticking) return;                 // 上一 tick 未结束，跳过避免重入
            _ticking = true;
            try
            {
                var now = DateTime.UtcNow;
                if (now < _autoPausedUntil) return;   // 手动暂停期内不自动套用
                if (now < _cooldownUntil) return;     // 冷却期内不套用
                if (!DecisionTreeLearner.HasAnyPredictor) return; // 无 FastTree 模型且 k-NN 样本不足
                if (!_stateInited) return;            // 还不知道当前电源/音频，无法比较

                var (power, audio) = DecisionTreeLearner.Predict(s);
                if (string.IsNullOrEmpty(power) && string.IsNullOrEmpty(audio)) return;

                // 防抖：预测需连续 5s 不变
                bool same = string.Equals(power, _pendingPower, StringComparison.OrdinalIgnoreCase)
                         && string.Equals(audio, _pendingAudio, StringComparison.OrdinalIgnoreCase);
                if (same) _stableCount++; else { _pendingPower = power; _pendingAudio = audio; _stableCount = 1; }
                if (_stableCount < StableSeconds) return;

                // 与当前不同才套用
                bool needPower = !string.IsNullOrEmpty(power) && !string.Equals(power, _currentPower, StringComparison.OrdinalIgnoreCase);
                bool needAudio = !string.IsNullOrEmpty(audio) && !string.Equals(audio, _currentAudio, StringComparison.OrdinalIgnoreCase);
                if (!needPower && !needAudio) { _stableCount = 0; return; }

                ApplyAuto(s, needPower ? power : null, needAudio ? audio : null);
            }
            catch (Exception ex) { AppLog.Log("Learn", "infer fail: " + ex.Message); }
            finally { _ticking = false; }
        }

        static void ApplyAuto(FeatureCollector.Snapshot s, string power, string audio)
        {
            var now = DateTime.UtcNow;
            _cooldownUntil = now.AddSeconds(CooldownSeconds);
            _autoApplyGraceUntil = now.AddSeconds(AutoApplyGraceSeconds);
            _stableCount = 0;

            AppLog.Log("Learn", $"auto-apply: exe={s.ExeName} power={(power ?? "-")} audio={(audio ?? "-")}");
            try
            {
                if (power != null) { PowerPlanService.SetActivePlan(power); _currentPower = power; }
                if (audio != null) { AudioDevices.SetDefaultDevice(audio); _currentAudio = audio; }
            }
            catch (Exception ex) { AppLog.Log("Learn", "apply fail: " + ex.Message); }

            if (Notify)
            {
                AppProfileToast.Show(
                    string.IsNullOrEmpty(s.ExeName) ? "当前情境" : s.ExeName,
                    power != null ? FriendlyPowerName(power) : null,
                    audio != null ? FriendlyAudioName(audio) : null);
            }
        }

        // 样本达阈值自动训练；首次达 AutoTrainThreshold 训练，之后每 +RetrainEvery 条且距上次≥5min 重训。
        // 手动训练由设置面板直接调 TrainAsync。计数/时间在 OnTrained 按实际完成时刷新，避免训练途中误判。
        static void MaybeAutoTrain()
        {
            if (_training) return;
            int n = SampleStore.Count;
            if (n < DecisionTreeLearner.AutoTrainThreshold) return;
            bool firstTime = _lastAutoTrainCount < DecisionTreeLearner.AutoTrainThreshold;
            if (!firstTime && (n - _lastAutoTrainCount) < DecisionTreeLearner.RetrainEvery) return;
            var now = DateTime.UtcNow;
            if (!firstTime && now - _lastTrainAt < TimeSpan.FromMinutes(MinRetrainMinutes)) return;
            _training = true;
            DecisionTreeLearner.Trained -= OnTrained;
            DecisionTreeLearner.Trained += OnTrained;
            DecisionTreeLearner.TrainAsync();
        }

        static void OnTrained(DecisionTreeLearner.ModelMeta meta)
        {
            _lastAutoTrainCount = SampleStore.Count;
            _lastTrainAt = DateTime.UtcNow;
            _training = false;
            try { DecisionTreeLearner.Trained -= OnTrained; } catch { }
            if (meta != null)
                AppLog.Log("Learn", $"trained: n={meta.SampleCount} power={meta.PowerAccuracy:0.00} audio={meta.AudioAccuracy:0.00}");
            else
                AppLog.Log("Learn", "trained: no-op/failed");
        }

        // ---- 设置面板：手动训练 / 重置 ----
        public static void TrainNow()
        {
            if (_training) return;
            _training = true;
            DecisionTreeLearner.Trained -= OnTrained;
            DecisionTreeLearner.Trained += OnTrained;
            DecisionTreeLearner.TrainAsync();
        }

        public static void ResetModel()
        {
            DecisionTreeLearner.DeleteModels();
            _lastAutoTrainCount = 0;
        }

        public static void ClearSamplesAndModel()
        {
            SampleStore.Clear();
            ResetModel();
        }

        public static bool IsTraining => _training;
        public static int SampleCount => SampleStore.Count;

        // ---- 友好名（Toast + 设置面板共用），迁移自旧 AppProfileService ----
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
    }
}
