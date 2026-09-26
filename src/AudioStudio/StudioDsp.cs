using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Runtime.InteropServices;
using NAudio.Dsp;

namespace PowerAudioManager.AudioStudio;

internal readonly record struct DenoiserMetrics(double InferenceMilliseconds, string Label);

internal interface IStudioDenoiser : IDisposable
{
    void Initialize() { }
    void Process(float[] frame, float strength = 1);
    void Reset() { }
    double LastInferenceMilliseconds => 0;
    string MetricLabel => "Inference";
    DenoiserMetrics GetMetrics() => new(LastInferenceMilliseconds, MetricLabel);
}

internal enum DenoiseMode { Off, Eco, Balanced, Quality }

internal static class StudioDenoisers
{
    // New models implement IStudioDenoiser and register a factory here. All providers
    // consume 480 normalized mono samples at 48 kHz and preserve state across calls.
    public static readonly IReadOnlyDictionary<string, Func<IStudioDenoiser>> Factories =
        new Dictionary<string, Func<IStudioDenoiser>>(StringComparer.Ordinal)
        {
            [nameof(DenoiseMode.Off)] = () => new BypassDenoiser(),
            [nameof(DenoiseMode.Eco)] = () => new RnNoiseDenoiser(),
            [nameof(DenoiseMode.Balanced)] = () => new GtcrnDenoiser(),
            [nameof(DenoiseMode.Quality)] = () => new DeepFilterDenoiser()
        };
    public static IStudioDenoiser Create(string name)
    {
        if (!Factories.TryGetValue(name, out var factory)) throw new NotSupportedException("Unknown denoising model: " + name);
        if (name != nameof(DenoiseMode.Off))
        {
            if (!Environment.Is64BitProcess) throw new NotSupportedException("降噪需要 x64 版 OneBox / Denoising requires OneBox x64");
            if (!NativeLibrary.TryLoad("vcruntime140.dll", out var runtime))
                throw new InvalidOperationException("请安装 Microsoft Visual C++ x64 运行库，见使用教程 / Install the Microsoft Visual C++ x64 runtime from the guide");
            NativeLibrary.Free(runtime);
        }
        var denoiser = factory(); denoiser.Initialize(); return denoiser;
    }
}

internal sealed class BypassDenoiser : IStudioDenoiser
{
    public void Process(float[] frame, float strength = 1) { }
    public void Dispose() { }
}

internal sealed class RnNoiseDenoiser : IStudioDenoiser
{
    readonly RNNoise.NET.Denoiser _model = new();
    readonly float[] _dry = new float[480], _previous = new float[480];
    public double LastInferenceMilliseconds { get; private set; }
    public void Process(float[] frame, float strength = 1)
    {
        Array.Copy(frame, _dry, 480);
        var clock = Stopwatch.StartNew();
        _model.Denoise(frame);
        LastInferenceMilliseconds = clock.Elapsed.TotalMilliseconds;
        // RNNoise's STFT has one frame of delay. Align the dry branch before blending.
        for (int i = 0; i < 480; i++) frame[i] = frame[i] * strength + _previous[i] * (1 - strength);
        Array.Copy(_dry, _previous, 480);
    }
    public void Dispose() => _model.Dispose();
}

internal sealed class StudioDsp : IDisposable
{
    public const int Rate = 48000, Frame = 480;
    public static readonly float[] Frequencies = { 31, 62, 125, 250, 500, 1000, 2000, 4000, 8000, 16000 };
    readonly IStudioDenoiser _denoiser;
    readonly BiQuadFilter[] _eq = new BiQuadFilter[10];
    readonly float[] _eqGains = new float[10];
    readonly float[] _wet = new float[Frame];
    readonly float[] _voice = new float[Frame];
    float _limiterGain = 1;
    bool _eqWasEnabled;
    public DenoiserMetrics ModelMetrics => _denoiser.GetMetrics();
    public StudioDsp(IStudioDenoiser denoiser) { _denoiser = denoiser; Array.Fill(_eqGains, float.NaN); }

    // Monitor is tapped before BGM except at Final. Music retains its stereo image.
    public void Process(float[] microphone, float[] music, float[] output, float[] monitor, StudioSettings s)
    {
        for (int i = 0; i < Frame; i++) _voice[i] = s.Microphone && float.IsFinite(microphone[i]) ? microphone[i] : 0;
        Tap(0);
        if (s.Denoise && s.Microphone)
        {
            Array.Copy(_voice, _wet, Frame);
            _denoiser.Process(_wet, s.Strength);
            Array.Copy(_wet, _voice, Frame);
        }
        Tap(1);
        for (int i = 0; i < Frame; i++) _voice[i] *= s.MicGain;
        Tap(2);
        if (s.Eq)
        {
            for (int b = 0; b < 10; b++)
            {
                if (!_eqWasEnabled || _eqGains[b] != s.Bands[b])
                {
                    _eq[b] = BiQuadFilter.PeakingEQ(Rate, Frequencies[b], 1.1f, s.Bands[b]);
                    _eqGains[b] = s.Bands[b];
                }
                for (int i = 0; i < Frame; i++) _voice[i] = _eq[b].Transform(_voice[i]);
            }
        }
        _eqWasEnabled = s.Eq;
        Tap(3);
        for (int i = 0; i < Frame; i++)
        {
            float voice = _voice[i];
            if (s.Explode)
            {
                float clipped = Math.Clamp(voice * (1 + 39 * s.ExplodeStrength), -.8f, .8f);
                voice = voice * (1 - s.ExplodeStrength) + clipped * s.ExplodeStrength;
            }
            float l = voice + (s.Music ? music[i * 2] * s.MusicGain : 0);
            float r = voice + (s.Music ? music[i * 2 + 1] * s.MusicGain : 0);
            if (!float.IsFinite(l)) l = 0; if (!float.IsFinite(r)) r = 0;
            float peak = Math.Max(Math.Abs(l), Math.Abs(r));
            float target = peak > .95f ? .95f / peak : 1;
            _limiterGain = target < _limiterGain ? target : Math.Min(target, _limiterGain + .0002f);
            output[i * 2] = Math.Clamp(l * _limiterGain, -.95f, .95f);
            output[i * 2 + 1] = Math.Clamp(r * _limiterGain, -.95f, .95f);
        }
        if (s.MonitorPoint == 4) Array.Copy(output, monitor, output.Length);
        void Tap(int point)
        {
            if (s.MonitorPoint != point) return;
            for (int i = 0; i < Frame; i++) monitor[i * 2] = monitor[i * 2 + 1] = _voice[i];
        }
    }
    public void Dispose() => _denoiser.Dispose();
}

// A bounded stereo FIFO. Independent device clocks are corrected by interpolating
// one stereo frame per block at high/low watermarks instead of dropping large chunks.
internal sealed class StudioFifo
{
    readonly float[] _samples = new float[48000 * 2];
    readonly object _gate = new();
    int _read, _count;
    public void Write(ReadOnlySpan<float> values)
    {
        lock (_gate)
        {
            foreach (float sample in values)
            {
                if (_count == _samples.Length) { _read = (_read + 2) % _samples.Length; _count -= 2; }
                _samples[(_read + _count++) % _samples.Length] = float.IsFinite(sample) ? sample : 0;
            }
        }
    }
    public void Clear() { lock (_gate) { _read = 0; _count = 0; } }
    public void Read(float[] destination)
    {
        lock (_gate)
        {
            // Bound stale audio after suspend or a slow inference frame to 60 ms.
            if (_count > 5760) { int discard = (_count - 2880) & ~1; _read = (_read + discard) % _samples.Length; _count -= discard; }
            int frames = destination.Length / 2;
            int available = _count / 2;
            int consume = Math.Min(available, frames);
            if (available > frames * 3) consume = frames + 1;
            else if (available >= frames && available < frames + 16) consume = frames - 1;
            if (consume >= frames - 1 && consume > 1)
            {
                for (int frame = 0; frame < frames; frame++)
                {
                    double position = frame * (consume - 1d) / (frames - 1);
                    int a = (int)position, b = Math.Min(a + 1, consume - 1); float fraction = (float)(position - a);
                    for (int ch = 0; ch < 2; ch++)
                    {
                        float x = _samples[(_read + a * 2 + ch) % _samples.Length], y = _samples[(_read + b * 2 + ch) % _samples.Length];
                        destination[frame * 2 + ch] = x + (y - x) * fraction;
                    }
                }
            }
            else
            {
                for (int i = 0; i < consume * 2; i++) destination[i] = _samples[(_read + i) % _samples.Length];
                Array.Clear(destination, consume * 2, destination.Length - consume * 2);
            }
            _read = (_read + consume * 2) % _samples.Length; _count -= consume * 2;
        }
    }
}
