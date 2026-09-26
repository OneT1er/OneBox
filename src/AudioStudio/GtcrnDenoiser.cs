using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Numerics;
using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;

namespace PowerAudioManager.AudioStudio;

// GTCRN's public streaming graph accepts 512-point spectra at 16 kHz with a
// 256-sample hop. Resampling and overlap-add stay inside this adapter so the
// rest of the Studio pipeline always processes 480 samples at 48 kHz.
internal sealed class GtcrnDenoiser : IStudioDenoiser
{
    readonly InferenceSession _session;
    readonly Queue<float> _input = new(), _output = new(), _dry = new();
    readonly float[] _filter = new float[63], _history = new float[63], _overlap = new float[512];
    readonly float[] _window = new float[512];
    float[] _conv = new float[2 * 1 * 16 * 16 * 33];
    float[] _tra = new float[2 * 3 * 1 * 1 * 16];
    float[] _inter = new float[2 * 1 * 33 * 16];
    int _historyPosition, _decimation;
    float _lastOutput;
    public double LastInferenceMilliseconds { get; private set; }

    public GtcrnDenoiser()
    {
        string path = Path.Combine(AppContext.BaseDirectory, "AudioStudio", "Models", "gtcrn_simple.onnx");
        if (!File.Exists(path)) throw new FileNotFoundException("GTCRN model is missing", path);
        _session = new InferenceSession(path, new SessionOptions { IntraOpNumThreads = 1, InterOpNumThreads = 1 });
        for (int i = 0; i < 512; i++) _window[i] = (float)Math.Sqrt(.5 * (1 - Math.Cos(2 * Math.PI * i / 511)));
        double sum = 0;
        for (int i = 0; i < _filter.Length; i++)
        {
            int offset = i - 31;
            double sinc = offset == 0 ? 1d / 3 : Math.Sin(Math.PI * offset / 3) / (Math.PI * offset);
            _filter[i] = (float)(sinc * (.54 - .46 * Math.Cos(2 * Math.PI * i / 62)));
            sum += _filter[i];
        }
        for (int i = 0; i < _filter.Length; i++) _filter[i] /= (float)sum;
        for (int i = 0; i < 1440; i++) _dry.Enqueue(0);
    }

    public void Process(float[] frame, float strength = 1)
    {
        LastInferenceMilliseconds = 0;
        if (frame.Length != 480) throw new ArgumentException("GTCRN requires a 480-sample frame.", nameof(frame));
        foreach (float sample in frame)
        {
            _dry.Enqueue(sample);
            _history[_historyPosition] = sample;
            _historyPosition = (_historyPosition + 1) % _history.Length;
            if (++_decimation != 3) continue;
            _decimation = 0;
            float filtered = 0;
            for (int j = 0; j < _filter.Length; j++)
                filtered += _history[(_historyPosition + j) % _history.Length] * _filter[j];
            _input.Enqueue(filtered);
        }
        while (_input.Count >= 512) Infer();
        float blend = Math.Clamp(strength, 0, 1);
        for (int i = 0; i < 160; i++)
        {
            float next = _output.Count > 0 ? _output.Dequeue() : 0;
            for (int phase = 0; phase < 3; phase++)
            {
                int index = i * 3 + phase;
                float wet = _lastOutput + (next - _lastOutput) * (phase + 1) / 3f;
                frame[index] = wet * blend + _dry.Dequeue() * (1 - blend);
            }
            _lastOutput = next;
        }
    }

    void Infer()
    {
        var samples = _input.Take(512).ToArray();
        for (int i = 0; i < 256; i++) _input.Dequeue();
        var spectrum = new Complex[512];
        for (int i = 0; i < 512; i++) spectrum[i] = samples[i] * _window[i];
        Transform(spectrum, false);
        var mix = new float[257 * 2];
        for (int i = 0; i <= 256; i++) { mix[i * 2] = (float)spectrum[i].Real; mix[i * 2 + 1] = (float)spectrum[i].Imaginary; }
        var inputs = new[]
        {
            NamedOnnxValue.CreateFromTensor("mix", new DenseTensor<float>(mix, new[] { 1, 257, 1, 2 })),
            NamedOnnxValue.CreateFromTensor("conv_cache", new DenseTensor<float>(_conv, new[] { 2, 1, 16, 16, 33 })),
            NamedOnnxValue.CreateFromTensor("tra_cache", new DenseTensor<float>(_tra, new[] { 2, 3, 1, 1, 16 })),
            NamedOnnxValue.CreateFromTensor("inter_cache", new DenseTensor<float>(_inter, new[] { 2, 1, 33, 16 }))
        };
        var clock = Stopwatch.StartNew();
        using var results = _session.Run(inputs);
        LastInferenceMilliseconds += clock.Elapsed.TotalMilliseconds;
        var values = results.ToArray();
        var enhanced = values[0].AsTensor<float>().ToArray();
        _conv = values[1].AsTensor<float>().ToArray();
        _tra = values[2].AsTensor<float>().ToArray();
        _inter = values[3].AsTensor<float>().ToArray();
        for (int i = 0; i <= 256; i++) spectrum[i] = new Complex(enhanced[i * 2], enhanced[i * 2 + 1]);
        for (int i = 257; i < 512; i++) spectrum[i] = Complex.Conjugate(spectrum[512 - i]);
        Transform(spectrum, true);
        for (int i = 0; i < 512; i++) _overlap[i] += (float)spectrum[i].Real * _window[i];
        for (int i = 0; i < 256; i++) _output.Enqueue(_overlap[i]);
        Array.Copy(_overlap, 256, _overlap, 0, 256);
        Array.Clear(_overlap, 256, 256);
    }

    static void Transform(Complex[] values, bool inverse)
    {
        int n = values.Length;
        for (int i = 1, j = 0; i < n; i++)
        {
            int bit = n >> 1;
            for (; (j & bit) != 0; bit >>= 1) j ^= bit;
            j ^= bit;
            if (i < j) (values[i], values[j]) = (values[j], values[i]);
        }
        for (int len = 2; len <= n; len <<= 1)
        {
            double angle = (inverse ? 2 : -2) * Math.PI / len;
            Complex step = new(Math.Cos(angle), Math.Sin(angle));
            for (int start = 0; start < n; start += len)
            {
                Complex factor = Complex.One;
                for (int j = 0; j < len / 2; j++)
                {
                    Complex a = values[start + j], b = values[start + j + len / 2] * factor;
                    values[start + j] = a + b; values[start + j + len / 2] = a - b;
                    factor *= step;
                }
            }
        }
        if (inverse) for (int i = 0; i < n; i++) values[i] /= n;
    }
    public void Dispose() => _session.Dispose();
}
