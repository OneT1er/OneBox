using System;
using System.Diagnostics;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using NAudio.CoreAudioApi;
using NAudio.Wave;

namespace PowerAudioManager.AudioStudio;

internal sealed class StudioEngine : IDisposable
{
    readonly StudioFifo _microphone = new(), _music = new();
    readonly BufferedWaveProvider _output = Buffer(), _monitor = Buffer();
    WasapiRecorder _micCapture, _musicCapture;
    WasapiPlayer _player, _monitorPlayer;
    MMDevice _inputDevice, _outputDevice, _monitorDevice;
    StudioDsp _dsp;
    Thread _thread;
    volatile bool _running;
    volatile StudioSettings _settings;
    public string Error { get; private set; }
    public string Note { get; private set; } = "";
    public float InputPeak, OutputPeak;
    public float MusicPeak;
    public float[] SpectrumFrame = new float[480];
    public bool Running => _running;
    public int CapturedPid { get; private set; }
    public string InputId => _inputDevice?.ID ?? "";
    public string MonitorId => _monitorDevice?.ID ?? "";
    public bool MonitorFaulted { get; private set; }
    public string MonitorError { get; private set; }
    public bool ProcessingTooSlow { get; private set; }
    static BufferedWaveProvider Buffer() => new(WaveFormat.CreateIeeeFloatWaveFormat(48000, 2), TimeSpan.FromMilliseconds(200))
        { DiscardOnBufferOverflow = true, ReadFully = true };

    public async Task StartAsync(StudioSettings settings, int pid)
    {
        _settings = settings.Copy();
        try
        {
            using var enumerator = new MMDeviceEnumerator();
            _outputDevice = enumerator.GetDevice(settings.OutputId);
            if (_outputDevice.DataFlow != DataFlow.Render || _outputDevice.State != DeviceState.Active)
                throw new InvalidOperationException("输出设备不可用 / Output device unavailable");
            _dsp = new StudioDsp(StudioDenoisers.Create(settings.Model));
            if (settings.Microphone && string.IsNullOrEmpty(settings.InputId))
                Note = "没有可用麦克风，音乐仍可共享 / No microphone available; music sharing remains active";
            if (settings.Microphone && !string.IsNullOrEmpty(settings.InputId))
            {
                try
                {
                    _inputDevice = enumerator.GetDevice(settings.InputId);
                    if (_inputDevice.State != DeviceState.Active) throw new InvalidOperationException("Microphone disconnected");
                    _micCapture = new WasapiRecorderBuilder().WithDevice(_inputDevice).WithSharedMode()
                        .WithFormat(WaveFormat.CreateIeeeFloatWaveFormat(48000, 2)).Build();
                    _micCapture.DataAvailable += (bytes, flags, _, _) => Capture(_microphone, bytes, flags);
                    _micCapture.RecordingStopped += (_, args) => { if (args.Exception != null) Error = args.Exception.Message; };
                    _micCapture.StartRecording();
                }
                catch (Exception ex)
                {
                    _micCapture?.Dispose(); _micCapture = null; _inputDevice?.Dispose(); _inputDevice = null;
                    Note = "麦克风未连接，音乐仍可共享 / Microphone unavailable; music sharing remains active";
                    AppLog.Log("AudioStudio microphone", ex.Message);
                }
            }
            if (settings.Music && pid > 0)
            {
                // This MUST be process loopback. Never substitute desktop loopback.
                _musicCapture = await new WasapiRecorderBuilder().WithProcessLoopback((uint)pid, ProcessLoopbackMode.IncludeTargetProcessTree)
                    .WithFormat(WaveFormat.CreateIeeeFloatWaveFormat(48000, 2)).BuildAsync().ConfigureAwait(false);
                _musicCapture.DataAvailable += (bytes, flags, _, _) => Capture(_music, bytes, flags);
                _musicCapture.RecordingStopped += (_, args) => { if (args.Exception != null) Error = args.Exception.Message; };
                _musicCapture.StartRecording(); CapturedPid = pid;
            }
            else if (settings.Music) Note = "等待所选软件播放 / Waiting for selected application";
            _player = new WasapiPlayerBuilder().WithDevice(_outputDevice).WithSharedMode().WithEventSync().WithLatency(40).Build();
            _player.Init(_output);
            _player.PlaybackStopped += (_, args) => { if (args.Exception != null) Error = args.Exception.Message; };
            // A shared USB container is common for headsets and does not mean that
            // WASAPI cannot open both endpoints. Let the driver decide.
            if (settings.Monitor)
            {
                try
                {
                if (string.IsNullOrEmpty(settings.MonitorId) || settings.MonitorId == settings.OutputId)
                    throw new InvalidOperationException("请为监听选择独立耳机 / Choose separate headphones for monitoring");
                _monitorDevice = enumerator.GetDevice(settings.MonitorId);
                if (new StudioDevice(_monitorDevice.ID, _monitorDevice.FriendlyName).IsCable)
                    throw new InvalidOperationException("监听不能输出到虚拟麦克风 / Monitor cannot feed a virtual microphone");
                _monitorPlayer = new WasapiPlayerBuilder().WithDevice(_monitorDevice).WithSharedMode().WithEventSync().WithLatency(60).Build();
                _monitorPlayer.Init(_monitor);
                _monitorPlayer.PlaybackStopped += (_, args) =>
                {
                    if (args.Exception == null) return;
                    MonitorError = args.Exception.Message; MonitorFaulted = true;
                };
                }
                catch (Exception ex)
                {
                    _monitorPlayer?.Dispose(); _monitorPlayer = null; _monitorDevice?.Dispose(); _monitorDevice = null;
                    MonitorError = ex.Message;
                    Note = "监听不可用：" + ex.Message + " / Monitoring unavailable: " + ex.Message;
                    AppLog.Log("AudioStudio monitor", ex.Message);
                }
            }
            _running = true;
            _thread = new Thread(ProcessLoop) { IsBackground = true, Name = "OneBox audio DSP", Priority = ThreadPriority.AboveNormal };
            _thread.Start(); _player.Play(); _monitorPlayer?.Play();
        }
        catch { Dispose(); throw; }
    }
    static void Capture(StudioFifo fifo, ReadOnlySpan<byte> bytes, AudioClientBufferFlags flags)
    {
        if ((flags & AudioClientBufferFlags.Silent) != 0) return;
        fifo.Write(MemoryMarshal.Cast<byte, float>(bytes));
    }
    public void Update(StudioSettings settings)
    {
        var previous = _settings;
        _settings = settings.Copy();
        if (previous.Music != settings.Music) _music.Clear();
    }
    void ProcessLoop()
    {
        var stereoMic = new float[960]; var mono = new float[480]; var music = new float[960];
        var output = new float[960]; var monitor = new float[960]; var bytes = new byte[3840];
        try
        {
            int slowFrames = 0;
            while (_running)
            {
                if (_output.BufferedBytes >= bytes.Length * 3) { Thread.Sleep(2); continue; }
                _microphone.Read(stereoMic); _music.Read(music);
                float musicPeak = 0;
                for (int i = 0; i < music.Length; i++) musicPeak = Math.Max(musicPeak, Math.Abs(music[i]));
                MusicPeak = musicPeak;
                float peak = 0;
                for (int i = 0; i < 480; i++) { mono[i] = (stereoMic[i * 2] + stereoMic[i * 2 + 1]) * .5f; peak = Math.Max(peak, Math.Abs(mono[i])); }
                InputPeak = peak;
                var settings = _settings;
                var processing = Stopwatch.StartNew();
                _dsp.Process(mono, music, output, monitor, settings);
                if (processing.Elapsed.TotalMilliseconds > 10)
                {
                    if (++slowFrames >= 10) ProcessingTooSlow = true;
                }
                else slowFrames = 0;
                peak = 0;
                for (int i = 0; i < 480; i++) { SpectrumFrame[i] = (output[i * 2] + output[i * 2 + 1]) * .5f; peak = Math.Max(peak, Math.Max(Math.Abs(output[i * 2]), Math.Abs(output[i * 2 + 1]))); }
                OutputPeak = peak;
                System.Buffer.BlockCopy(output, 0, bytes, 0, bytes.Length); _output.AddSamples(bytes, 0, bytes.Length);
                if (_monitorPlayer != null)
                {
                    // Monitoring is optional and cannot backpressure the virtual microphone.
                    for (int i = 0; i < monitor.Length; i++) monitor[i] = Math.Clamp(monitor[i] * .7f, -.8f, .8f);
                    System.Buffer.BlockCopy(monitor, 0, bytes, 0, bytes.Length);
                    if (_monitor.BufferedBytes > bytes.Length * 6) _monitor.ClearBuffer();
                    _monitor.AddSamples(bytes, 0, bytes.Length);
                }
            }
        }
        catch (Exception ex) { Error = ex.Message; AppLog.Log("AudioStudio DSP", ex); }
        finally { _running = false; }
    }
    public void Dispose()
    {
        _running = false;
        _thread?.Join(3000);
        _micCapture?.Dispose(); _musicCapture?.Dispose();
        _player?.Dispose(); _monitorPlayer?.Dispose();
        _dsp?.Dispose(); _inputDevice?.Dispose(); _outputDevice?.Dispose(); _monitorDevice?.Dispose();
        _micCapture = null; _musicCapture = null; _player = null; _monitorPlayer = null; _dsp = null;
    }
}
