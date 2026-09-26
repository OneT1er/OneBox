using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;

namespace PowerAudioManager.AudioStudio;

internal sealed record StudioBenchmarkResult(string Mode, string MetricLabel, double Inference, double Pipeline,
    double P50, double P95, double Maximum, double Rtf);

internal static class StudioBenchmark
{
    public static StudioBenchmarkResult Run(string mode)
    {
        using var dsp = new StudioDsp(StudioDenoisers.Create(mode));
        var settings = new StudioSettings { Model = mode, Denoise = mode != nameof(DenoiseMode.Off) };
        var mic = new float[480]; var music = new float[960];
        var output = new float[960]; var monitor = new float[960];
        var random = new Random(39);
        var samples = new List<double>(300);
        double inference = 0;
        string metricLabel = "Inference";
        for (int frame = 0; frame < 350; frame++)
        {
            for (int i = 0; i < mic.Length; i++)
                mic[i] = .08f * (float)Math.Sin(2 * Math.PI * 220 * (frame * 480 + i) / 48000)
                    + (float)(random.NextDouble() - .5) * .02f;
            var clock = Stopwatch.StartNew();
            dsp.Process(mic, music, output, monitor, settings);
            if (frame < 50) continue;
            samples.Add(clock.Elapsed.TotalMilliseconds);
            var metrics = dsp.ModelMetrics;
            inference += metrics.InferenceMilliseconds;
            metricLabel = metrics.Label;
        }
        samples.Sort();
        double average = samples.Average();
        return new StudioBenchmarkResult(mode, metricLabel, inference / samples.Count, average,
            samples[(int)(samples.Count * .5)], samples[(int)(samples.Count * .95)],
            samples[^1], average / 10);
    }
}
