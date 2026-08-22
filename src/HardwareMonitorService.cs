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
        private readonly List<MetricValue> _allPipeMetrics = new();
        private CancellationTokenSource _pipeCancellation;
        private Task _pipeTask;
        private NamedPipeClientStream _activePipe;
        private bool _started;

        public bool IsAvailable { get; private set; }
        public float? CpuTemperature { get; private set; }
        public float? GpuTemperature { get; private set; }
        public List<SensorInfo> AllTempSensors { get; } = new();
        public List<SensorInfo> AllFanSensors { get; } = new();
        public List<SensorInfo> AllControlSensors { get; } = new();
        public List<string> EnabledMetrics { get; private set; } = new();
        public List<MetricValue> ActiveMetrics { get; } = new();

        private HardwareMonitorService() { }

        private static bool IsType(string value, string expected) =>
            string.Equals(value, expected, StringComparison.OrdinalIgnoreCase);

        private static bool IsGpuType(string value) =>
            IsType(value, "GpuNvidia") || IsType(value, "GpuAmd") || IsType(value, "GpuIntel");

        public void Start()
        {
            lock (_gate)
            {
                if (_started) return;
                _started = true;
            }
            LoadEnabledMetrics();
            _pipeCancellation = new CancellationTokenSource();
            _pipeTask = RunPipeClientAsync(_pipeCancellation.Token);
        }

        private async Task RunPipeClientAsync(CancellationToken cancellationToken)
        {
            var backoff = new ReconnectBackoff();
            while (!cancellationToken.IsCancellationRequested)
            {
                NamedPipeClientStream client = null;
                try
                {
                    using var identity = WindowsIdentity.GetCurrent(TokenAccessLevels.Query);
                    string userSid = identity.User?.Value ?? throw new InvalidOperationException("Current user SID is unavailable.");
                    client = new NamedPipeClientStream(".", PipeNames.ForHardware(userSid), PipeDirection.InOut,
                        PipeOptions.Asynchronous, TokenImpersonationLevel.Impersonation);
                    lock (_gate) _activePipe = client;
                    await client.ConnectAsync((int)IpcProtocol.ConnectTimeout.TotalMilliseconds, cancellationToken).ConfigureAwait(false);
                    PipeServerIdentityVerifier.EnsureLocalSystemServer(client);

                    IpcRequest request = IpcRequest.Create(IpcCommand.SubscribeHardware,
                        new HardwareSubscribePayload { MinimumIntervalMilliseconds = 500 });
                    await IpcFraming.WriteAsync(client, request, cancellationToken).ConfigureAwait(false);
                    backoff.Reset();
                    AppLog.Log("Temp", "authenticated hardware pipe connected");

                    while (client.IsConnected && !cancellationToken.IsCancellationRequested)
                    {
                        IpcResponse response = await IpcFraming.ReadAsync<IpcResponse>(client, cancellationToken,
                            TimeSpan.FromSeconds(75)).ConfigureAwait(false);
                        if (!response.Success)
                            throw new IpcProtocolException(response.ErrorCode, response.ErrorMessage ?? "Hardware helper rejected the request.");
                        if (response.Version != IpcProtocol.Version || response.RequestId != request.RequestId || response.Command != IpcCommand.HardwareSnapshot)
                            throw new IpcProtocolException(IpcErrorCode.InvalidMessage, "Hardware response envelope is invalid.");
                        HardwareSnapshot snapshot = response.ReadResult<HardwareSnapshot>();
                        if (snapshot != null) ApplySnapshot(snapshot);
                    }
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { break; }
                catch (Exception) when (cancellationToken.IsCancellationRequested) { break; }
                catch (Exception ex) { AppLog.Log("Temp", "hardware pipe rejected/disconnected: " + ex.Message); }
                finally
                {
                    lock (_gate)
                    {
                        if (ReferenceEquals(_activePipe, client)) _activePipe = null;
                        IsAvailable = false;
                    }
                    try { client?.Dispose(); } catch { }
                }

                try { await Task.Delay(backoff.NextDelay(), cancellationToken).ConfigureAwait(false); }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { break; }
            }
        }

        private void ApplySnapshot(HardwareSnapshot snapshot)
        {
            bool createDefaults;
            lock (_gate)
            {
                CpuTemperature = snapshot.CpuTemperature;
                GpuTemperature = snapshot.GpuTemperature;
                IsAvailable = snapshot.Ready;

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
                createDefaults = EnabledMetrics.Count == 0 && string.IsNullOrWhiteSpace(AppPrefs.GetString("Monitor.Metrics", ""));
            }

            if (createDefaults) CreateDefaultMetrics();
            UpdateFromSnapshot();
        }

        private void LoadEnabledMetrics()
        {
            string raw = AppPrefs.GetString("Monitor.Metrics", "");
            EnabledMetrics = raw.Split(';', StringSplitOptions.RemoveEmptyEntries)
                .Select(value => value.Trim()).Where(value => value.Length > 0).ToList();
        }

        private void CreateDefaultMetrics()
        {
            List<string> defaults;
            lock (_gate)
            {
                SensorInfo cpu = AllTempSensors.FirstOrDefault(sensor => IsType(sensor.HwType, "Cpu"));
                SensorInfo gpu = AllTempSensors.FirstOrDefault(sensor => IsGpuType(sensor.HwType));
                defaults = new List<string>();
                if (cpu != null) defaults.Add(EncodeConfig(cpu, "CPU"));
                if (gpu != null) defaults.Add(EncodeConfig(gpu, "GPU"));
                if (EnabledMetrics.Count != 0 || defaults.Count == 0) return;
                EnabledMetrics = defaults;
            }
            if (!AppPrefs.SetString("Monitor.Metrics", string.Join(";", defaults)))
            {
                lock (_gate) EnabledMetrics.Clear();
            }
        }

        public void Update()
        {
            if (IsAvailable) UpdateFromSnapshot();
        }

        private void UpdateFromSnapshot()
        {
            lock (_gate)
            {
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
                }
                ActiveMetrics.Clear();
                ActiveMetrics.AddRange(values);
            }
        }

        private MetricValue FindSnapshotMetric(SensorInfo config, string exactKey)
        {
            MetricValue exact = _allPipeMetrics.FirstOrDefault(metric => metric.ConfigKey == exactKey);
            if (exact != null) return exact;
            List<MetricValue> matches = _allPipeMetrics.Where(metric => MetricMatches(metric, config)).ToList();
            return matches.Count == 1 ? matches[0] : null;
        }

        private static bool MetricMatches(MetricValue metric, SensorInfo config)
        {
            string[] parts = (metric.ConfigKey ?? "").Split('|');
            return parts.Length >= 3
                && string.Equals(parts[0], config.SensorType, StringComparison.OrdinalIgnoreCase)
                && string.Equals(parts[2], config.SensorName, StringComparison.Ordinal)
                && string.Equals(parts[1], config.HardwareName, StringComparison.Ordinal);
        }

        public float? ReadSensorPreview(SensorInfo config)
        {
            if (config == null) return null;
            lock (_gate)
            {
                List<MetricValue> matches = _allPipeMetrics.Where(metric => MetricMatches(metric, config)).ToList();
                return matches.Count == 1 ? matches[0].Value : null;
            }
        }

        public bool SaveEnabledMetrics(List<string> list)
        {
            var next = list?.ToList() ?? new List<string>();
            if (!AppPrefs.SetString("Monitor.Metrics", string.Join(";", next))) return false;
            lock (_gate) EnabledMetrics = next;
            UpdateFromSnapshot();
            return true;
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
            CancellationTokenSource cancellation;
            Task task;
            NamedPipeClientStream activePipe;
            lock (_gate)
            {
                if (!_started) return;
                _started = false;
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
                IsAvailable = false;
                CpuTemperature = null;
                GpuTemperature = null;
                AllTempSensors.Clear();
                AllFanSensors.Clear();
                AllControlSensors.Clear();
                ActiveMetrics.Clear();
                _allPipeMetrics.Clear();
                _lastMetrics.Clear();
            }
        }

        public void Dispose() => Stop();
    }
}
