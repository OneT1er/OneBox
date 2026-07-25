using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.ML;
using Microsoft.ML.Data;
using Microsoft.ML.Transforms;
using Microsoft.ML.Trainers.FastTree;

namespace PowerAudioManager
{
    /// <summary>
    /// 阶段3：决策树训练与推理（ML.NET）。用 FastTree 二分类 + OneVersusAll 包装成多分类，
    /// 分别为「电源计划」「音频设备」各训练一棵（组）树。样本取自 OneBox.samples.csv，
    /// 80% 训练 / 20% 验证得出准确率，最终在全集上重训后保存为 .zip。推理时加载模型预测最可能的选择。
    /// 训练在线程池后台执行（FastTree 在数百样本上秒级完成）。模型与元数据存 exe 同目录。
    /// </summary>
    public static class DecisionTreeLearner
    {
        // 训练输入行：所有特征 + 两个候选标签列（训练电源模型时用 PowerPlan 作 Label，音频用 AudioDevice）。
        public class LearnRow
        {
            public float Cpu { get; set; }
            public float Gpu { get; set; }        // -1=不可用
            public float Fullscreen { get; set; }  // 0/1
            public float Battery { get; set; }     // 0/1
            public float Hour { get; set; }
            public float Category { get; set; }  // 0=Other 1=Game 2=Creative 3=VideoConf
            public string Exe { get; set; }      // 前台 exe 无扩展名；one-hot 入特征，让模型区分"同类别不同应用"的偏好（如不同游戏选不同音频设备）
            public string PowerPlan { get; set; }
            public string AudioDevice { get; set; }
        }

        public class LearnPred
        {
            [ColumnName("PredictedLabel")] public string Label { get; set; }
        }

        public class ModelMeta
        {
            public int SampleCount { get; set; }
            public double PowerAccuracy { get; set; }    // 0-1，-1=未训练(单类)
            public double AudioAccuracy { get; set; }
            public int PowerClasses { get; set; }
            public int AudioClasses { get; set; }
            public DateTime TrainedAt { get; set; }
        }

        /// <summary>训练完成后触发（后台线程），设置面板可据此刷新。</summary>
        public static event Action<ModelMeta> Trained;

        public const int MinSamplesToTrain = 30;    // 手动训练按钮的最小样本数
        public const int AutoTrainThreshold = 50;   // 自动触发 FastTree 训练的样本数（原 200，观察式采样下数小时即可达）
        public const int RetrainEvery = 25;         // 首次自动训练后，每累积这么多条重训一次
        public const int MinSamplesToInfer = 20;    // k-NN 回退预测最低样本数：FastTree 未就绪时即可工作，消除冷启动空窗

        static readonly object _lock = new object();
        static MLContext _ml;
        static PredictionEngine<LearnRow, LearnPred> _powerEngine;
        static PredictionEngine<LearnRow, LearnPred> _audioEngine;
        static bool _powerAvail, _audioAvail;
        static bool _loaded;

        // ---- k-NN 回退预测缓存（FastTree 模型未就绪时使用，从 ~20 条样本即可工作）----
        // 冷启动期（样本 < AutoTrainThreshold 或某目标只有 1 类没训成 FastTree）用 k-NN 兜底，
        // 消除旧版"满 200 条前完全不自动切"的空窗。样本数变化时按需重载。
        static List<SampleStore.Sample> _knnCache;
        static int _knnCacheCount = -1;
        const int KnnK = 7;   // 近邻数

        static string Dir
        {
            get
            {
                var exe = Environment.ProcessPath;
                return string.IsNullOrEmpty(exe) ? AppDomain.CurrentDomain.BaseDirectory : Path.GetDirectoryName(exe);
            }
        }
        static string PowerModelPath => Path.Combine(Dir, "OneBox.learn.power.zip");
        static string AudioModelPath => Path.Combine(Dir, "OneBox.learn.audio.zip");
        static string MetaPath => Path.Combine(Dir, "OneBox.learn.meta.json");

        public static bool HasPowerModel { get { lock (_lock) return _powerAvail; } }
        public static bool HasAudioModel { get { lock (_lock) return _audioAvail; } }
        public static bool IsLoaded => _loaded;

        /// <summary>是否存在可用预测器（FastTree 模型 或 k-NN 回退样本已够）。LearningEngine 推理门用。</summary>
        public static bool HasAnyPredictor
        {
            get
            {
                lock (_lock) { if (_powerAvail || _audioAvail) return true; }
                return SampleStore.Count >= MinSamplesToInfer;
            }
        }

        public static ModelMeta LoadMeta()
        {
            try
            {
                if (!File.Exists(MetaPath)) return null;
                return JsonSerializer.Deserialize<ModelMeta>(File.ReadAllText(MetaPath));
            }
            catch { return null; }
        }

        /// <summary>启动时加载已存在的模型（若样本已达阈值）。线程池后台执行，避免阻塞 UI。</summary>
        public static void Load()
        {
            if (_loaded) return;
            _loaded = true;
            System.Threading.ThreadPool.QueueUserWorkItem(_ =>
            {
                try { LoadEngines(); } catch (Exception ex) { AppLog.Log("Learn", "load fail: " + ex.Message); }
            });
        }

        static void LoadEngines()
        {
            lock (_lock)
            {
                _ml ??= new MLContext(seed: 0);
                _powerEngine = LoadEngine(PowerModelPath, out _powerAvail);
                _audioEngine = LoadEngine(AudioModelPath, out _audioAvail);
            }
            AppLog.Log("Learn", $"loaded: power={_powerAvail} audio={_audioAvail}");
        }

        static PredictionEngine<LearnRow, LearnPred> LoadEngine(string path, out bool ok)
        {
            ok = false;
            try
            {
                if (!File.Exists(path)) return null;
                var model = _ml.Model.Load(path, out var schema);
                ok = true;
                return _ml.Model.CreatePredictionEngine<LearnRow, LearnPred>(model, schema);
            }
            catch (Exception ex) { AppLog.Log("Learn", "load engine fail: " + path + ": " + ex.Message); return null; }
        }

        /// <summary>异步训练（线程池）。成功后保存模型、加载引擎、触发 Trained 事件。</summary>
        public static void TrainAsync()
        {
            System.Threading.ThreadPool.QueueUserWorkItem(_ =>
            {
                ModelMeta meta = null;
                try { meta = Train(); }
                catch (Exception ex) { AppLog.Log("Learn", "train fail: " + ex.Message); }
                // 失败/空操作也触发，让订阅者复位 _training 与按钮状态。
                try { Trained?.Invoke(meta); } catch { }
            });
        }

        /// <summary>同步训练（应在后台线程调用）。返回元数据；样本不足或异常返回 null。</summary>
        public static ModelMeta Train()
        {
            var samples = SampleStore.LoadAll();
            if (samples.Count < MinSamplesToTrain)
            {
                AppLog.Log("Learn", $"train skip: only {samples.Count} samples (need {MinSamplesToTrain})");
                return null;
            }

            var rows = samples.Select(ToRow).ToList();
            double powerAcc = -1, audioAcc = -1;
            int powerClasses = rows.Select(r => r.PowerPlan).Distinct().Count();
            int audioClasses = rows.Select(r => r.AudioDevice).Distinct().Count();

            lock (_lock)
            {
                _ml ??= new MLContext(seed: 0);
                // 电源模型：至少 2 个不同电源计划才有意义。
                if (powerClasses >= 2)
                {
                    powerAcc = TrainOne(rows, nameof(LearnRow.PowerPlan), PowerModelPath);
                    _powerEngine = LoadEngine(PowerModelPath, out _powerAvail);
                }
                else { AppLog.Log("Learn", "power: only 1 class, skip"); }
                // 音频模型：至少 2 个不同音频设备。
                if (audioClasses >= 2)
                {
                    audioAcc = TrainOne(rows, nameof(LearnRow.AudioDevice), AudioModelPath);
                    _audioEngine = LoadEngine(AudioModelPath, out _audioAvail);
                }
                else { AppLog.Log("Learn", "audio: only 1 class, skip"); }
            }

            var meta = new ModelMeta
            {
                SampleCount = samples.Count,
                PowerAccuracy = powerAcc,
                AudioAccuracy = audioAcc,
                PowerClasses = powerClasses,
                AudioClasses = audioClasses,
                TrainedAt = DateTime.Now,
            };
            try { File.WriteAllText(MetaPath, JsonSerializer.Serialize(meta, new JsonSerializerOptions { WriteIndented = true })); } catch { }
            AppLog.Log("Learn", $"trained: n={meta.SampleCount} powerAcc={powerAcc:0.00}({powerClasses}类) audioAcc={audioAcc:0.00}({audioClasses}类)");
            return meta;
        }

        // 训练单个目标：80% 训练 + 20% 验证算准确率，再在全集重训保存。返回验证集准确率(0-1)。
        static double TrainOne(List<LearnRow> rows, string labelCol, string modelPath)
        {
            var data = _ml.Data.LoadFromEnumerable(rows);
            var split = _ml.Data.TrainTestSplit(data, testFraction: 0.2, seed: 0);

            // 公共管道：标签转 Key + exe one-hot（区分同类别不同应用的偏好）+ 拼特征 + FastTree(OneVersusAll 多分类)。
            // 验证集用 core（PredictedLabel 保持 key 类型供 Evaluate）；最终模型在 core 末尾追加 MapKeyToValue，
            // 预测时直接返回原始字符串标签，免去再查 key 映射。
            var core = _ml.Transforms.Conversion.MapValueToKey(outputColumnName: "Label", inputColumnName: labelCol)
                .Append(_ml.Transforms.Categorical.OneHotEncoding(
                    outputColumnName: "ExeFeat", inputColumnName: nameof(LearnRow.Exe),
                    outputKind: OneHotEncodingEstimator.OutputKind.Binary))
                .Append(_ml.Transforms.Concatenate("Features",
                    nameof(LearnRow.Cpu), nameof(LearnRow.Gpu), nameof(LearnRow.Fullscreen),
                    nameof(LearnRow.Battery), nameof(LearnRow.Hour), nameof(LearnRow.Category), "ExeFeat"))
                .Append(_ml.MulticlassClassification.Trainers.OneVersusAll(
                    _ml.BinaryClassification.Trainers.FastTree(new FastTreeBinaryTrainer.Options
                    {
                        LabelColumnName = "Label",
                        FeatureColumnName = "Features",
                        NumberOfTrees = 30,
                        NumberOfLeaves = 24,
                    })));

            var evalModel = core.Fit(split.TrainSet);
            var evalPred = evalModel.Transform(split.TestSet);
            var metrics = _ml.MulticlassClassification.Evaluate(evalPred, labelColumnName: "Label", predictedLabelColumnName: "PredictedLabel");
            double acc = metrics.MicroAccuracy;

            var finalModel = core.Append(_ml.Transforms.Conversion.MapKeyToValue("PredictedLabel")).Fit(data);
            using (var fs = File.Create(modelPath))
                _ml.Model.Save(finalModel, data.Schema, fs);

            return acc;
        }

        /// <summary>预测电源/音频。FastTree 模型优先；某目标无模型时用 k-NN 回退（样本不足则该项返回 null）。</summary>
        public static (string power, string audio) Predict(FeatureCollector.Snapshot s)
        {
            if (s == null) return (null, null);
            var row = ToRowFromSnapshot(s);
            string power = null, audio = null;
            lock (_lock)
            {
                if (_powerAvail && _powerEngine != null)
                {
                    try { power = _powerEngine.Predict(row)?.Label; } catch (Exception ex) { AppLog.Log("Learn", "predict power fail: " + ex.Message); }
                }
                if (_audioAvail && _audioEngine != null)
                {
                    try { audio = _audioEngine.Predict(row)?.Label; } catch (Exception ex) { AppLog.Log("Learn", "predict audio fail: " + ex.Message); }
                }
            }
            // FastTree 未覆盖的目标用 k-NN 回退，冷启动期也能给出预测。
            if (string.IsNullOrEmpty(power)) power = KnnPredictSafe(s, true);
            if (string.IsNullOrEmpty(audio)) audio = KnnPredictSafe(s, false);
            return (power, audio);
        }

        // ---- k-NN 回退预测 ----
        // 保证缓存与磁盘样本同步：样本数变化时重载（观察式采样每 ~45s 追加一条，重载开销可忽略）。
        static List<SampleStore.Sample> KnnSamples()
        {
            int n = SampleStore.Count;
            if (_knnCache == null || n != _knnCacheCount)
            {
                _knnCache = SampleStore.LoadAll();
                _knnCacheCount = n;
            }
            return _knnCache;
        }

        static string KnnPredictSafe(FeatureCollector.Snapshot s, bool power)
        {
            try
            {
                var samples = KnnSamples();
                if (samples == null || samples.Count < MinSamplesToInfer) return null;
                return KnnPredict(samples, s, power ? (Func<SampleStore.Sample, string>)(x => x.PowerPlan) : (x => x.AudioDevice));
            }
            catch (Exception ex) { AppLog.Log("Learn", "knn fail: " + ex.Message); return null; }
        }

        // 距离加权 k-NN：算每条历史样本到当前快照的情境距离，取最近 K 个，按 1/(1+d) 加权投票。
        // exe 名命中给强负偏置——"这个应用上次选了什么"是最有用信号，盖过瞬时负载差异。
        static string KnnPredict(List<SampleStore.Sample> samples, FeatureCollector.Snapshot s, Func<SampleStore.Sample, string> label)
        {
            var scored = new List<(double w, string lab)>(samples.Count);
            foreach (var sm in samples)
            {
                string lab = label(sm) ?? "";
                if (lab.Length == 0) continue;
                scored.Add((1.0 / (1.0 + FeatureDistance(sm, s)), lab));
            }
            if (scored.Count == 0) return null;
            int k = Math.Min(KnnK, scored.Count);
            scored.Sort((a, b) => b.w.CompareTo(a.w));   // 权重大 = 距离小 = 更近，降序取前 k
            var votes = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);
            for (int i = 0; i < k; i++)
            {
                var t = scored[i];
                if (votes.TryGetValue(t.lab, out var v)) votes[t.lab] = v + t.w;
                else votes[t.lab] = t.w;
            }
            string best = null; double bestW = -1;
            foreach (var kv in votes) if (kv.Value > bestW) { bestW = kv.Value; best = kv.Key; }
            return best;
        }

        // 情境距离：电池/时间/进程类别权重高（情境上下文），CPU/GPU 次之，全屏再次。
        // exe 名命中给 -2.0 强负偏置（同应用历史样本优先），距离下限 0。
        static double FeatureDistance(SampleStore.Sample sm, FeatureCollector.Snapshot s)
        {
            double cpu = Math.Abs(sm.Cpu - s.CpuLoad) / 100.0;
            double smGpu = sm.Gpu < 0 ? 0 : sm.Gpu, sGpu = s.GpuLoad < 0 ? 0 : s.GpuLoad;
            double gpu = Math.Abs(smGpu - sGpu) / 100.0;
            double fs = (sm.Fullscreen != 0) != s.Fullscreen ? 1 : 0;
            double bat = (sm.Battery != 0) != s.OnBattery ? 1 : 0;
            double dh = Math.Abs(sm.Hour - s.Hour);
            if (dh > 12) dh = 24 - dh;     // 时间环形距离（23 点与 1 点相近）
            dh /= 12.0;
            double cat = Math.Abs(CategoryIndex(sm.Category) - s.CategoryIndex) / 3.0;
            double d = cpu + gpu * 0.8 + fs * 0.5 + bat * 1.5 + dh * 1.2 + cat;
            if (!string.IsNullOrEmpty(s.ExeName) && string.Equals(sm.Exe, s.ExeName, StringComparison.OrdinalIgnoreCase))
                d -= 2.0;
            return d < 0 ? 0 : d;
        }

        static int CategoryIndex(string category) =>
            Enum.TryParse<AppCategory>(category, out var c) ? (int)c : 0;

        // 删除模型与元数据（设置面板"重置模型"）。
        public static void DeleteModels()
        {
            lock (_lock)
            {
                _powerEngine = null; _audioEngine = null;
                _powerAvail = false; _audioAvail = false;
            }
            try { if (File.Exists(PowerModelPath)) File.Delete(PowerModelPath); } catch { }
            try { if (File.Exists(AudioModelPath)) File.Delete(AudioModelPath); } catch { }
            try { if (File.Exists(MetaPath)) File.Delete(MetaPath); } catch { }
            AppLog.Log("Learn", "models deleted");
        }

        static LearnRow ToRow(SampleStore.Sample s) => new LearnRow
        {
            Cpu = s.Cpu,
            Gpu = s.Gpu,
            Fullscreen = s.Fullscreen,
            Battery = s.Battery,
            Hour = s.Hour,
            Category = Enum.TryParse<AppCategory>(s.Category, out var c) ? (float)c : 0f,
            Exe = s.Exe ?? "",
            PowerPlan = s.PowerPlan ?? "",
            AudioDevice = s.AudioDevice ?? "",
        };

        static LearnRow ToRowFromSnapshot(FeatureCollector.Snapshot s) => new LearnRow
        {
            Cpu = s.CpuLoad,
            Gpu = s.GpuLoad,
            Fullscreen = s.Fullscreen ? 1 : 0,
            Battery = s.OnBattery ? 1 : 0,
            Hour = s.Hour,
            Category = s.CategoryIndex,
            Exe = s.ExeName ?? "",
            PowerPlan = "",   // 预测时不提供标签
            AudioDevice = "",
        };
    }
}
