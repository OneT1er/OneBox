using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Threading;
using NAudio.CoreAudioApi;
using PowerAudioManager.Commands;

namespace PowerAudioManager.AudioStudio;

internal sealed class StudioController : IDisposable
{
    public static StudioController Instance { get; } = new();
    public StudioSettings Settings { get; private set; } = StudioSettings.Load();
    public StudioEngine Engine { get; private set; }
    public bool Wanted { get; private set; }
    public bool Busy { get; private set; }
    public string Status { get; private set; } = "就绪 / Ready";
    public event Action Changed;
    readonly SemaphoreSlim _gate = new(1);
    readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromSeconds(3) };
    StudioWindow _window;
    bool _disposed, _initialized, _polling;
    DateTime _lastUpdate = DateTime.MinValue;
    CancellationTokenSource _updateCancellation;
    public string T(string zh, string en) => Settings.Language == "en" ? en : zh;
    public void Initialize()
    {
        if (_initialized) return; _initialized = true;
        _timer.Tick += async (_, _) => await PollAsync(); _timer.Start();
        if (Settings.AutoStartAudio) _ = StartAsync();
    }
    public void Show(MainWindow owner)
    {
        Initialize();
        if (_window == null) { _window = new StudioWindow(owner, this); _window.Closed += (_, _) => _window = null; }
        _window.Show(); _window.Activate();
    }
    public void Apply(StudioSettings settings)
    {
        settings.Normalize();
        if (!settings.Save()) throw new InvalidOperationException(T("音频设置保存失败", "Could not save audio settings"));
        Settings = settings.Copy(); Engine?.Update(Settings); Changed?.Invoke();
    }
    public async Task ToggleAsync(string feature)
    {
        var s = Settings.Copy();
        switch (feature)
        {
            case "Denoise":
                s.Denoise = !s.Denoise;
                if (s.Denoise && s.Model == nameof(DenoiseMode.Off)) s.Model = nameof(DenoiseMode.Balanced);
                break;
            case "Eq": s.Eq = !s.Eq; break;
            case "Music": s.Music = !s.Music; break;
            case "Explode": s.Explode = !s.Explode; break;
            case "Monitor": s.Monitor = !s.Monitor; break;
        }
        bool modelChanged = s.Model != Settings.Model;
        Apply(s);
        if (Wanted && (feature == "Music" || feature == "Monitor" || modelChanged)) await StartAsync();
    }
    public async Task StartAsync()
    {
        _updateCancellation?.Cancel();
        Wanted = true;
        await _gate.WaitAsync();
        try
        {
            if (_disposed || !Wanted) return;
            Busy = true; Status = T("正在连接音频设备…", "Connecting audio devices…"); Changed?.Invoke();
            var old = Engine; Engine = null;
            if (old != null) await Task.Run(old.Dispose);
            var settings = Settings.Copy();
            if (settings.Microphone && string.IsNullOrEmpty(settings.InputId))
            {
                settings.InputId = await Task.Run(StudioDevices.DefaultMicrophone);
                if (settings.InputId.Length > 0) Apply(settings);
            }
            var outputs = await Task.Run(() => StudioDevices.List(DataFlow.Render));
            if (string.IsNullOrEmpty(settings.OutputId))
            {
                settings.OutputId = outputs.FirstOrDefault(x => x.IsVbCableInput)?.Id ?? "";
                if (!string.IsNullOrEmpty(settings.OutputId)) Apply(settings);
            }
            var selected = outputs.FirstOrDefault(x => x.Id == settings.OutputId);
            if (selected == null) throw new InvalidOperationException(T("未找到虚拟输出设备。请安装 VB-CABLE，并选择 CABLE Input。", "Virtual output missing. Install VB-CABLE and select CABLE Input."));
            if (!selected.IsVbCableInput) throw new InvalidOperationException(T("共享输出请选择 VB-CABLE 的 CABLE Input；耳机请在监听中选择。", "Choose VB-CABLE CABLE Input for sharing; choose headphones under Monitor."));
            if (settings.Music && !OperatingSystem.IsWindowsVersionAtLeast(10, 0, 20348))
                throw new NotSupportedException(T("按应用共享需要支持 Process Loopback 的 Windows，建议 Windows 11。", "Application sharing requires Process Loopback support. Windows 11 is recommended."));
            int pid = settings.Music ? ResolveApplication(settings, await Task.Run(StudioDevices.Applications)) : 0;
            if (pid > 0 && settings.ApplicationPid != pid)
            {
                settings.ApplicationPid = pid;
                Apply(settings);
            }
            var engine = new StudioEngine();
            await Task.Run(() => engine.StartAsync(settings, pid));
            if (_disposed || !Wanted) { await Task.Run(engine.Dispose); return; }
            Engine = engine; Status = engine.Note.Length > 0 ? engine.Note : T("正在共享 · 关闭窗口后继续运行", "Sharing · continues when this window closes");
        }
        catch (Exception ex) { Status = ex.Message; AppLog.Log("AudioStudio start", ex); }
        finally { Busy = false; _gate.Release(); Changed?.Invoke(); }
    }
    public async Task StopAsync()
    {
        Wanted = false;
        await _gate.WaitAsync();
        try { var engine = Engine; Engine = null; if (engine != null) await Task.Run(engine.Dispose); Status = T("已停止 · 通话请切回实体麦克风", "Stopped · select your physical microphone in chat"); }
        finally { _gate.Release(); Changed?.Invoke(); }
    }
    async Task PollAsync()
    {
        if (_disposed || Busy || _polling) return;
        _polling = true;
        try
        {
            if (Wanted)
            {
                bool reconnect = Engine == null || !Engine.Running || Engine.Error != null;
                if (!reconnect)
                {
                    var inputs = await Task.Run(() => StudioDevices.List(DataFlow.Capture));
                    bool available = inputs.Any(x => x.Id == Settings.InputId);
                    reconnect = Settings.Microphone && available != !string.IsNullOrEmpty(Engine.InputId);
                    if (Settings.Monitor)
                    {
                        var outputs = await Task.Run(() => StudioDevices.List(DataFlow.Render));
                        bool monitorAvailable = outputs.Any(x => x.Id == Settings.MonitorId && !x.IsCable);
                        reconnect |= monitorAvailable != !string.IsNullOrEmpty(Engine.MonitorId) || Engine.MonitorFaulted;
                    }
                    if (Settings.Music)
                    {
                        int pid = ResolveApplication(Settings, await Task.Run(StudioDevices.Applications));
                        reconnect |= pid != Engine.CapturedPid;
                    }
                }
                if (reconnect && Wanted && !Busy) await StartAsync();
            }
            // Reuse the OneBox signed-package update workflow. Never restart an active call.
            if (!Wanted && Settings.AutoUpdate && DateTime.UtcNow - _lastUpdate > TimeSpan.FromHours(6))
            {
                _lastUpdate = DateTime.UtcNow;
                using var cancellation = new CancellationTokenSource();
                _updateCancellation = cancellation;
                var workflow = new UpdateWorkflow(new VelopackUpdateClient(), new UpdateServiceCoordinator());
                var check = await workflow.CheckAsync(cancellation.Token);
                if (check.Success && check.UpdateAvailable && !Wanted)
                    await workflow.DownloadAndApplyAsync(check.Candidate, new Progress<int>(), cancellation.Token);
                _updateCancellation = null;
            }
        }
        catch (Exception ex) { AppLog.Log("AudioStudio recovery", ex); }
        finally { _updateCancellation = null; _polling = false; }
    }
    public void Dispose()
    {
        _disposed = true; Wanted = false; _timer.Stop();
        _updateCancellation?.Cancel();
        Engine?.Dispose(); Engine = null;
        _window?.Close();
    }
    internal static int ResolveApplication(StudioSettings settings, StudioApplication[] applications)
    {
        if (string.IsNullOrWhiteSpace(settings.ApplicationPath)) return 0;
        var candidates = applications.Where(x => string.Equals(x.Path, settings.ApplicationPath, StringComparison.OrdinalIgnoreCase)).ToArray();
        var audible = candidates.OrderByDescending(x => x.Peak).FirstOrDefault(x => x.Peak > .00001f);
        return audible?.Pid ?? candidates.FirstOrDefault(x => x.Pid == settings.ApplicationPid && x.Active)?.Pid
            ?? candidates.FirstOrDefault(x => x.Active)?.Pid
            ?? candidates.FirstOrDefault(x => x.Pid == settings.ApplicationPid)?.Pid
            ?? candidates.FirstOrDefault()?.Pid ?? 0;
    }
}
