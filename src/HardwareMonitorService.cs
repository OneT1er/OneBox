using System;
using System.Collections.Generic;
using System.IO.Pipes;
using System.Linq;
using System.Security.Principal;
using System.Threading;
using System.Threading.Tasks;
using OneBox.Contracts;

namespace PowerAudioManager
{
    public class SensorInfo
    {
        public string HardwareName { get; set; }
        public string SensorName { get; set; }
        public string HwType { get; set; }
        public string SensorType { get; set; }
        public override string ToString() => $"{HardwareName} - {SensorName}";
    }

    public class MetricValue
    {
        public string DisplayName;
        public string IconKey;
        public float? Value;
        public string Unit;
        public bool IsTemp => Unit == "°C";
        public string ConfigKey;
        public bool Cached;
    }

    /// <summary>
    /// GUI-side hardware facade. Hardware access is always isolated in
    /// OneBox.Hardware; this type only consumes authenticated pipe snapshots.
    /// </summary>
    public sealed class HardwareMonitorService : IDisposable
    {
        public static readonly HardwareMonitorService Instance = new();

        private readonly object _gate = new();
        private readonly Dictionary<string, MetricValue> _lastMetrics = new();
        private readonly HashSet<string> _loggedRemaps = new();
        private readonly List<MetricValue> _allPipeMetrics = new();
        private CancellationTokenSource _pipeCancellation;
        private Task _pipeTask;
        private NamedPipeClientStream _activePipe;
        private bool _started;
        private int _pipeGeneration;
        private bool _monitorRequested;
        private int _catalogUsers;
        private int _subscriptionRevision;
        private CancellationTokenSource _subscriptionCancellation;
        private bool _loggedOldHelper;
        private bool _selectionLoaded;
        private readonly Func<string> _readMetrics;

        public bool CatalogAvailable { get; private set; }

        public bool IsAvailable { get; private set; }
        public float? CpuTemperature { get; private set; }
        public float? GpuTemperature { get; private set; }
        public List<SensorInfo> AllTempSensors { get; } = new();
        public List<SensorInfo> AllFanSensors { get; } = new();
        public List<SensorInfo> AllControlSensors { get; } = new();
        public List<string> EnabledMetrics { get; private set; } = new();
        public List<MetricValue> ActiveMetrics { get; } = new();

        internal HardwareMonitorService(Func<string> readMetrics = null)
        {
            _readMetrics = readMetrics ?? (() => AppPrefs.GetString("Monitor.Metrics", ""));
        }

        internal void PrepareSelection()
        {
            lock (_gate)
            {
                if (_selectionLoaded) return;
                EnabledMetrics = (_readMetrics() ?? "").Split(';', StringSplitOptions.RemoveEmptyEntries)
                    .Select(value => value.Trim()).Where(value => value.Length > 0).ToList();
                _selectionLoaded = true;
            }
        }

        private static bool IsType(string value, string expected) =>
            string.Equals(value, expected, StringComparison.OrdinalIgnoreCase);

        private static bool IsGpuType(string value) =>
            IsType(value, "GpuNvidia") || IsType(value, "GpuAmd") || IsType(value, "GpuIntel");

        public void Start()
        {
            lock (_gate) _monitorRequested = true;
            EnsureStarted();
        }

        private void EnsureStarted()
        {
            PrepareSelection();
            lock (_gate)
            {
                if (_started) return;
                _started = true;
                _pipeCancellation = new CancellationTokenSource();
                _pipeTask = RunPipeClientAsync(_pipeCancellation.Token, ++_pipeGeneration);
            }
        }

        public IDisposable BeginCatalogPreview()
        {
            lock (_gate) { _catalogUsers++; RefreshSubscriptionUnsafe(); }
            EnsureStarted();
            return new CatalogPreviewLease(this);
        }

        private void EndCatalogPreview()
        {
            bool stop;
            lock (_gate)
            {
                if (--_catalogUsers == 0)
                {
                    AllTempSensors.Clear(); AllFanSensors.Clear(); AllControlSensors.Clear();
                    _allPipeMetrics.Clear();
                    CatalogAvailable = false;
                }
                RefreshSubscriptionUnsafe();
                stop = !_monitorRequested && _catalogUsers == 0;
            }
            if (stop) StopCore();
        }

        private sealed class CatalogPreviewLease(HardwareMonitorService owner) : IDisposable
        {
            private HardwareMonitorService _owner = owner;
            public void Dispose() => Interlocked.Exchange(ref _owner, null)?.EndCatalogPreview();
        }

        private void RefreshSubscriptionUnsafe()
        {
            _subscriptionRevision++;
            try { _subscriptionCancellation?.Cancel(); } catch (ObjectDisposedException) { }
        }

        internal static HardwareSubscribePayload CreateSubscription(IEnumerable<string> keys, bool includeCatalog)
        {
            var payload = new HardwareSubscribePayload { SelectedSensors = new(), IncludeCatalog = includeCatalog };
            foreach (string key in keys)
            {
                SensorInfo sensor = DecodeConfig(key, out _, out _);
                if (sensor == null || payload.SelectedSensors.Any(item => IsType(item.SensorType, sensor.SensorType)
                    && item.HardwareName == sensor.HardwareName && item.SensorName == sensor.SensorName)) continue;
                payload.SelectedSensors.Add(new HardwareSensorIdentity { HardwareName = sensor.HardwareName, SensorName = sensor.SensorName, SensorType = sensor.SensorType });
            }
            return payload;
        }

        internal HardwareSubscribePayload CaptureSubscription(out int revision)
        {
            lock (_gate)
            {
                revision = _subscriptionRevision;
                return CreateSubscription(EnabledMetrics, _catalogUsers > 0);
            }
        }

        internal bool ApplySnapshotIfCurrent(HardwareSnapshot snapshot, int revision)
        {
            lock (_gate)
            {
                if (revision != _subscriptionRevision) return false;
                ApplySnapshot(snapshot);
                return true;
            }
        }

        private async Task RunPipeClientAsync(CancellationToken cancellationToken, int generation)
        {
            var backoff = new ReconnectBackoff();
            while (!cancellationToken.IsCancellationRequested)
            {
                NamedPipeClientStream client = null;
                int revision;
                HardwareSubscribePayload payload;
                using var subscriptionCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                lock (_gate)
                {
                    if (generation != _pipeGeneration || cancellationToken.IsCancellationRequested) return;
                    payload = CaptureSubscription(out revision);
                    _subscriptionCancellation = subscriptionCancellation;
                }
                CancellationToken subscriptionToken = subscriptionCancellation.Token;
                try
                {
                    using var identity = WindowsIdentity.GetCurrent(TokenAccessLevels.Query);
                    string userSid = identity.User?.Value ?? throw new InvalidOperationException("Current user SID is unavailable.");
                    client = new NamedPipeClientStream(".", PipeNames.ForHardware(userSid), PipeDirection.InOut,
                        PipeOptions.Asynchronous, TokenImpersonationLevel.Impersonation);
                    lock (_gate)
                    {
                        if (generation != _pipeGeneration || subscriptionToken.IsCancellationRequested) return;
                        _activePipe = client;
                    }
                    await client.ConnectAsync((int)IpcProtocol.ConnectTimeout.TotalMilliseconds, subscriptionToken).ConfigureAwait(false);
                    uint helperPid = PipeServerIdentityVerifier.EnsureLocalSystemServer(client);

                    IpcRequest request = IpcRequest.Create(IpcCommand.SubscribeHardware, payload);
                    await IpcFraming.WriteAsync(client, request, subscriptionToken).ConfigureAwait(false);
                    AppLog.Log("Temp", "authenticated hardware pipe connected");
                    bool loggedFirstSnapshot = false;

                    while (client.IsConnected && !cancellationToken.IsCancellationRequested)
                    {
                        HardwareSnapshotResponse response = await IpcFraming.ReadAsync<HardwareSnapshotResponse>(client, subscriptionToken,
                            TimeSpan.FromSeconds(75)).ConfigureAwait(false);
                        HardwareSnapshot snapshot = response.ReadSnapshot(request.RequestId);
                        if (snapshot != null)
                        {
                            if (!snapshot.SelectionApplied || (snapshot.Ready && snapshot.CatalogIncluded != payload.IncludeCatalog))
                            {
                                if (!_loggedOldHelper) { AppLog.Log("Temp", "Hardware helper requires a matching OneBox update; selected-sensor subscription is unavailable."); _loggedOldHelper = true; }
                                throw new IpcProtocolException(IpcErrorCode.UnsupportedVersion, "Hardware helper does not support selected sensors.");
                            }
                            lock (_gate)
                            {
                                if (generation != _pipeGeneration || subscriptionToken.IsCancellationRequested || !ReferenceEquals(_activePipe, client)
                                    || !ApplySnapshotIfCurrent(snapshot, revision)) continue;
                                int historyMetricCount = ActiveMetrics.Count(metric => metric.Value.HasValue && !metric.Cached && (metric.IsTemp || metric.Unit == "RPM"));
                                if (snapshot.Ready && historyMetricCount > 0 && !loggedFirstSnapshot)
                                {
                                    AppLog.Log("Temp", $"hardware first snapshot applied guiPid={Environment.ProcessId} helperPid={helperPid} connectionId={request.RequestId:N} appliedUtc={DateTime.UtcNow:O} historyMetricCount={historyMetricCount}");
                                    loggedFirstSnapshot = true;
                                }
                            }
                            backoff.Reset();
                        }
                    }
                }
                catch (Exception) when (subscriptionCancellation.IsCancellationRequested && !cancellationToken.IsCancellationRequested) { }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { break; }
                catch (Exception) when (cancellationToken.IsCancellationRequested) { break; }
                catch (IpcProtocolException ex) when (ex.ErrorCode == IpcErrorCode.UnsupportedVersion && _loggedOldHelper) { }
                catch (Exception ex) { AppLog.Log("Temp", "hardware pipe rejected/disconnected: " + ex.Message); }
                finally
                {
                    lock (_gate)
                    {
                        if (generation == _pipeGeneration && ReferenceEquals(_activePipe, client))
                        {
                            _activePipe = null;
                            InvalidateSnapshot();
                        }
                    }
                    try { client?.Dispose(); } catch { }
                }

                lock (_gate) { if (revision != _subscriptionRevision) { backoff.Reset(); continue; } }

                try { await Task.Delay(backoff.NextDelay(), subscriptionToken).ConfigureAwait(false); }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { break; }
                catch (OperationCanceledException) when (subscriptionToken.IsCancellationRequested) { backoff.Reset(); }
                finally
                {
                    lock (_gate)
                        if (ReferenceEquals(_subscriptionCancellation, subscriptionCancellation)) _subscriptionCancellation = null;
                }
            }
        }

        private void ApplySnapshot(HardwareSnapshot snapshot)
        {
            lock (_gate)
            {
                CpuTemperature = snapshot.CpuTemperature;
                GpuTemperature = snapshot.GpuTemperature;
                IsAvailable = snapshot.Ready;
                CatalogAvailable = snapshot.CatalogIncluded && _catalogUsers > 0;

                _allPipeMetrics.Clear();
                foreach (HardwareMetric metric in snapshot.Metrics ?? new List<HardwareMetric>())
                {
                    _allPipeMetrics.Add(new MetricValue
                    {
                        DisplayName = metric.Name,
                        IconKey = metric.Icon,
                        Value = metric.Value,
                        Unit = metric.Unit,
                        ConfigKey = metric.Key,
                    });
                }

                AllTempSensors.Clear();
                AllFanSensors.Clear();
                AllControlSensors.Clear();
                foreach (HardwareSensor sensor in snapshot.Sensors ?? new List<HardwareSensor>())
                {
                    var info = new SensorInfo
                    {
                        HardwareName = sensor.HardwareName,
                        SensorName = sensor.SensorName,
                        HwType = sensor.HardwareType,
                        SensorType = sensor.SensorType,
                    };
                    if (IsType(sensor.SensorType, "Fan")) AllFanSensors.Add(info);
                    else if (IsType(sensor.SensorType, "Control")) AllControlSensors.Add(info);
                    else AllTempSensors.Add(info);
                }
            }
            UpdateFromSnapshot();
        }

        public void Update()
        {
            UpdateFromSnapshot();
        }

        internal void InvalidateSnapshot()
        {
            lock (_gate)
            {
                IsAvailable = false;
                CpuTemperature = null;
                GpuTemperature = null;
                _allPipeMetrics.Clear();
                ActiveMetrics.Clear();
            }
        }

        private void UpdateFromSnapshot()
        {
            lock (_gate)
            {
                if (!IsAvailable) { ActiveMetrics.Clear(); return; }
                var values = new List<MetricValue>();
                foreach (string key in EnabledMetrics)
                {
                    SensorInfo config = DecodeConfig(key, out string displayName, out string iconKey);
                    if (config == null) continue;

                    MetricValue metric = FindSnapshotMetric(config, key);
                    if (metric?.Value != null)
                    {
                        var current = new MetricValue
                        {
                            DisplayName = displayName,
                            IconKey = iconKey,
                            Value = metric.Value,
                            Unit = metric.Unit,
                            ConfigKey = key,
                        };
                        _lastMetrics[key] = current;
                        values.Add(current);
                    }
                    else if (_lastMetrics.TryGetValue(key, out MetricValue last))
                    {
                        values.Add(new MetricValue
                        {
                            DisplayName = last.DisplayName,
                            IconKey = last.IconKey,
                            Value = last.Value,
                            Unit = last.Unit,
                            ConfigKey = last.ConfigKey,
                            Cached = true,
                        });
                    }
                    else
                    {
                        // Sensors such as DDR5 SPD temperatures can be present
                        // in enumeration for over a minute before their first
                        // usable value arrives. Keep the configured row visible
                        // as "--" instead of making the saved selection vanish.
                        values.Add(CreateUnavailableMetric(config, key, displayName, iconKey));
                    }
                }
                ActiveMetrics.Clear();
                ActiveMetrics.AddRange(values);
            }
        }

        internal static MetricValue CreateUnavailableMetric(SensorInfo config, string key, string displayName, string iconKey)
        {
            string unit = IsType(config?.SensorType, "Temperature") ? "°C"
                : IsType(config?.SensorType, "Control") ? "%" : "RPM";
            return new MetricValue
            {
                DisplayName = displayName,
                IconKey = iconKey,
                Value = null,
                Unit = unit,
                ConfigKey = key,
                Cached = true,
            };
        }

        private MetricValue FindSnapshotMetric(SensorInfo config, string exactKey)
        {
            MetricValue exact = string.IsNullOrEmpty(exactKey) ? null
                : _allPipeMetrics.FirstOrDefault(metric => string.Equals(metric.ConfigKey, exactKey, StringComparison.Ordinal));
            if (exact != null) return exact;
            MetricValue match = FindUniqueSnapshotMetric(_allPipeMetrics, config);
            if (match != null) return match;

            // LibreHardwareMonitor builds DDR5 hardware names from SPD data;
            // the model text/suffix may drift between cold boots. Resolve the
            // current sensor by unique type + SensorName, then bind its value.
            SensorInfo actual = ResolveSensor(GetSensorSourceUnsafe(config.SensorType), config);
            if (actual == null || SensorIdentityEquals(actual, config)) return null;
            match = FindUniqueSnapshotMetric(_allPipeMetrics, actual);
            if (match == null) return null;
            LogSensorRemap(config, actual);
            return match;
        }

        internal static MetricValue FindUniqueSnapshotMetric(List<MetricValue> metrics, SensorInfo config)
        {
            MetricValue match = null;
            foreach (MetricValue metric in metrics)
            {
                if (!MetricMatches(metric, config)) continue;
                if (match != null) return null;
                match = metric;
            }
            return match;
        }

        private static bool MetricMatches(MetricValue metric, SensorInfo config)
        {
            string sensorType = config.SensorType;
            string hardwareName = config.HardwareName;
            string sensorName = config.SensorName;
            if (sensorType == null || hardwareName == null || sensorName == null) return false;

            ReadOnlySpan<char> key = (metric.ConfigKey ?? "").AsSpan();
            int typeEnd = key.IndexOf('|');
            if (typeEnd < 0) return false;
            int hardwareEnd = key[(typeEnd + 1)..].IndexOf('|');
            if (hardwareEnd < 0) return false;
            hardwareEnd += typeEnd + 1;
            int nameEnd = key[(hardwareEnd + 1)..].IndexOf('|');
            if (nameEnd >= 0) nameEnd += hardwareEnd + 1;
            else nameEnd = key.Length;

            return key[..typeEnd].Equals(sensorType.AsSpan(), StringComparison.OrdinalIgnoreCase)
                && key[(typeEnd + 1)..hardwareEnd].Equals(hardwareName.AsSpan(), StringComparison.Ordinal)
                && key[(hardwareEnd + 1)..nameEnd].Equals(sensorName.AsSpan(), StringComparison.Ordinal);
        }

        private List<SensorInfo> GetSensorSourceUnsafe(string sensorType) =>
            IsType(sensorType, "Fan") ? AllFanSensors
            : IsType(sensorType, "Control") ? AllControlSensors
            : AllTempSensors;

        internal static SensorInfo ResolveSensor(IEnumerable<SensorInfo> source, SensorInfo config)
        {
            if (source == null || config == null) return null;
            List<SensorInfo> candidates = source.Where(sensor =>
                IsType(sensor.SensorType, config.SensorType)).ToList();
            SensorInfo exact = candidates.FirstOrDefault(sensor => SensorIdentityEquals(sensor, config));
            if (exact != null) return exact;

            List<SensorInfo> sameName = candidates.Where(sensor =>
                string.Equals(sensor.SensorName, config.SensorName, StringComparison.Ordinal)).ToList();
            return sameName.Count == 1 ? sameName[0] : null;
        }

        private static bool SensorIdentityEquals(SensorInfo left, SensorInfo right) =>
            left != null && right != null
            && IsType(left.SensorType, right.SensorType)
            && string.Equals(left.HardwareName, right.HardwareName, StringComparison.Ordinal)
            && string.Equals(left.SensorName, right.SensorName, StringComparison.Ordinal);

        private void LogSensorRemap(SensorInfo configured, SensorInfo actual)
        {
            string key = configured.SensorType + "|" + configured.HardwareName + "|" + configured.SensorName
                + "->" + actual.HardwareName + "|" + actual.SensorName;
            if (_loggedRemaps.Add(key))
                AppLog.Log("Temp", $"sensor remap: '{configured.HardwareName} | {configured.SensorName}' -> '{actual.HardwareName} | {actual.SensorName}'");
        }

        public float? ReadSensorPreview(SensorInfo config)
        {
            if (config == null) return null;
            lock (_gate)
            {
                return FindSnapshotMetric(config, null)?.Value;
            }
        }

        /// <summary>返回设置界面可安全枚举的传感器快照，避免管道刷新时修改集合。</summary>
        public List<SensorInfo> GetSensors(string sensorType)
        {
            lock (_gate)
            {
                List<SensorInfo> source = IsType(sensorType, "Fan") ? AllFanSensors
                    : IsType(sensorType, "Control") ? AllControlSensors
                    : AllTempSensors;
                return source.ToList();
            }
        }

        public bool IsSensorEnabled(SensorInfo sensor)
        {
            if (sensor == null) return false;
            lock (_gate)
            {
                foreach (string key in EnabledMetrics)
                {
                    SensorInfo configured = DecodeConfig(key, out _, out _);
                    if (configured == null || !IsType(configured.SensorType, sensor.SensorType)) continue;
                    SensorInfo resolved = ResolveSensor(GetSensorSourceUnsafe(configured.SensorType), configured);
                    if (SensorIdentityEquals(resolved, sensor)) return true;
                }
                return false;
            }
        }

        public bool SaveEnabledMetrics(List<string> list)
        {
            var next = list?.ToList() ?? new List<string>();
            if (!AppPrefs.SetString("Monitor.Metrics", string.Join(";", next))) return false;
            ApplyEnabledMetrics(next);
            PerfHistory.RetainEnabledSeries(next);
            UpdateFromSnapshot();
            return true;
        }

        internal void ApplyEnabledMetrics(List<string> next)
        {
            lock (_gate)
            {
                EnabledMetrics = next;
                foreach (string key in _lastMetrics.Keys.Where(key => !next.Contains(key, StringComparer.Ordinal)).ToList())
                    _lastMetrics.Remove(key);
                RefreshSubscriptionUnsafe();
            }
        }

        public static string EncodeConfig(SensorInfo sensor, string displayName) =>
            EncodeConfig(sensor, displayName, AutoIconKey(displayName, sensor));

        public static string EncodeConfig(SensorInfo sensor, string displayName, string iconKey) =>
            $"{sensor.SensorType}|{sensor.HardwareName}|{sensor.SensorName}|{displayName}|{iconKey}";

        public static SensorInfo DecodeConfig(string key, out string displayName, out string iconKey)
        {
            displayName = "";
            iconKey = "def";
            string[] parts = (key ?? "").Split('|');
            if (parts.Length < 3) return null;
            string type = string.IsNullOrEmpty(parts[0]) ? "Temperature" : parts[0];
            displayName = parts.Length >= 4 ? parts[3] : DefaultDisplayName(parts[1], parts[2], type);
            iconKey = parts.Length >= 5 ? parts[4] : AutoIconKey(displayName, null);
            return new SensorInfo { SensorType = type, HardwareName = parts[1], SensorName = parts[2] };
        }

        public static string AutoIconKey(string displayName, SensorInfo sensor)
        {
            string name = (displayName ?? "").ToLowerInvariant();
            string hardware = (sensor?.HardwareName ?? "").ToLowerInvariant();
            string sensorName = (sensor?.SensorName ?? "").ToLowerInvariant();
            if (sensor != null)
            {
                if (IsType(sensor.SensorType, "Fan")) return "fan";
                if (IsType(sensor.SensorType, "Control")) return "ctrl";
                if (sensorName.Contains("hot spot")) return "hot";
                if (sensorName.Contains("memory") || sensorName.Contains("junction")) return "vram";
                if (hardware.Contains("memory") || hardware.Contains("dram") || hardware.Contains("dimm") || hardware.Contains("ram")) return "dram";
                if (hardware.Contains("ssd") || hardware.Contains("hdd") || hardware.Contains("nvme") || hardware.Contains("disk")) return "disk";
                if (hardware.Contains("motherboard") || hardware.Contains("nuvoton") || hardware.Contains("ite ")) return "mb";
            }
            if (name.Contains("cpu") && !name.Contains("fan")) return "cpu";
            if (name.Contains("gpu") && !name.Contains("hot") && !name.Contains("vram") && !name.Contains("mem") && !name.Contains("fan")) return "gpu";
            if (name.Contains("hot")) return "hot";
            if (name.Contains("vram") || name.Contains("显存")) return "vram";
            if (name.Contains("内存") || name.Contains("dram") || name.Contains("ram")) return "dram";
            if (name.Contains("硬盘") || name.Contains("磁盘") || name.Contains("ssd") || name.Contains("disk")) return "disk";
            if (name.Contains("主板") || name.Contains("mb")) return "mb";
            if (name.Contains("fan") && !name.Contains("control") && !name.Contains("%")) return "fan";
            if (name.Contains("%") || name.Contains("control")) return "ctrl";
            return "def";
        }

        public static string DefaultDisplayName(string hardwareName, string sensorName, string sensorType)
        {
            string hardware = (hardwareName ?? "").ToLowerInvariant();
            string name = sensorName ?? "";
            bool cpu = hardware.Contains("cpu") || hardware.Contains("ryzen");
            bool gpu = hardware.Contains("nvidia") || hardware.Contains("geforce") || hardware.Contains("rtx") || hardware.Contains("radeon");
            if (IsType(sensorType, "Temperature"))
            {
                if (name.Contains("Hot Spot", StringComparison.OrdinalIgnoreCase)) return "GPU HotSpot";
                if (name.Contains("Memory", StringComparison.OrdinalIgnoreCase) || name.Contains("Junction", StringComparison.OrdinalIgnoreCase)) return "VRAM";
                if (cpu) return "CPU";
                if (gpu) return "GPU";
                return "Temp";
            }
            if (IsType(sensorType, "Fan")) return cpu ? "CPU Fan" : gpu ? "GPU Fan" : name;
            if (IsType(sensorType, "Control")) return cpu ? "CPU Fan%" : gpu ? "GPU Fan%" : name;
            return name;
        }

        public void Stop()
        {
            lock (_gate)
            {
                _monitorRequested = false;
                if (_catalogUsers > 0) return;
            }
            StopCore();
        }

        private void StopCore()
        {
            CancellationTokenSource cancellation;
            Task task;
            NamedPipeClientStream activePipe;
            int stoppedGeneration;
            lock (_gate)
            {
                if (!_started) return;
                _started = false;
                stoppedGeneration = ++_pipeGeneration;
                cancellation = _pipeCancellation;
                task = _pipeTask;
                activePipe = _activePipe;
                _pipeCancellation = null;
                _pipeTask = null;
                _activePipe = null;
            }
            // Signal cancellation before disposing the transport so a normal
            // rebuild/restart is observed as an intentional shutdown rather
            // than an EOF/rejected pipe failure in the client log.
            try { cancellation?.Cancel(); } catch { }
            try { activePipe?.Dispose(); } catch { }
            try { task?.Wait(2000); } catch { }
            cancellation?.Dispose();
            lock (_gate)
            {
                if (stoppedGeneration != _pipeGeneration) return;
                IsAvailable = false;
                CpuTemperature = null;
                GpuTemperature = null;
                AllTempSensors.Clear();
                AllFanSensors.Clear();
                AllControlSensors.Clear();
                ActiveMetrics.Clear();
                _allPipeMetrics.Clear();
                _lastMetrics.Clear();
                _loggedRemaps.Clear();
                CatalogAvailable = false;
            }
        }

        public void Dispose() => Stop();
    }
}
