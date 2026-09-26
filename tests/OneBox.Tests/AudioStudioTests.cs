using System;
using System.Linq;
using PowerAudioManager.AudioStudio;
using Xunit;

namespace OneBox.Tests;

public sealed class AudioStudioTests
{
    sealed class Identity : IStudioDenoiser
    {
        public void Process(float[] frame, float strength = 1) { }
        public void Dispose() { }
    }
    [Fact]
    public void StereoMusicIsPreservedAndDoesNotPassThroughVoiceEq()
    {
        using var dsp = new StudioDsp(new Identity());
        var music = new float[960]; for (int i = 0; i < 480; i++) { music[i * 2] = .2f; music[i * 2 + 1] = -.1f; }
        var output = new float[960];
        dsp.Process(new float[480], music, output, new float[960], new StudioSettings { Microphone = false, Music = true, MusicGain = 1, Eq = true, Bands = Enumerable.Repeat(12f, 10).ToArray() });
        Assert.Equal(.2f, output[500], 5); Assert.Equal(-.1f, output[501], 5);
    }
    [Fact]
    public void LimiterContainsOverloadIncludingDistortionAndInvalidSamples()
    {
        using var dsp = new StudioDsp(new Identity());
        var mic = Enumerable.Repeat(1f, 480).ToArray(); mic[0] = float.NaN;
        var music = Enumerable.Repeat(1f, 960).ToArray(); music[4] = float.PositiveInfinity;
        var output = new float[960];
        dsp.Process(mic, music, output, new float[960], new StudioSettings { Denoise = false, Music = true, MusicGain = 2, MicGain = 3, Explode = true, ExplodeStrength = 1 });
        Assert.All(output, x => Assert.True(float.IsFinite(x) && Math.Abs(x) <= .95001f));
    }
    [Fact]
    public void MonitorTapBeforeMixDoesNotContainMusic()
    {
        using var dsp = new StudioDsp(new Identity());
        var monitor = new float[960]; var output = new float[960];
        dsp.Process(new float[480], Enumerable.Repeat(.2f, 960).ToArray(), output, monitor,
            new StudioSettings { Denoise = false, Music = true, MonitorPoint = 3 });
        Assert.All(monitor, x => Assert.Equal(0, x)); Assert.True(output[100] > 0);
    }
    [Fact]
    public void MusicStopsWithoutLeakingQueuedSamples()
    {
        using var dsp = new StudioDsp(new Identity()); var output = new float[960];
        dsp.Process(new float[480], Enumerable.Repeat(.2f, 960).ToArray(), output, new float[960], new StudioSettings { Music = false });
        Assert.All(output, x => Assert.Equal(0, x));
    }
    [Fact]
    public void FifoBoundsLatencyAndClearsStoppedAudio()
    {
        var fifo = new StudioFifo(); fifo.Write(Enumerable.Repeat(.2f, 200000).ToArray());
        var frame = new float[960]; fifo.Read(frame); Assert.All(frame, x => Assert.Equal(.2f, x));
        fifo.Clear(); fifo.Read(frame); Assert.All(frame, x => Assert.Equal(0, x));
    }
    [Fact]
    public void CorruptSettingsAreSanitized()
    {
        var settings = new StudioSettings { Strength = float.NaN, MicGain = 100, Bands = null, MonitorPoint = 100 };
        settings.Normalize(); Assert.Equal(0, settings.Strength); Assert.Equal(3, settings.MicGain); Assert.Equal(10, settings.Bands.Length); Assert.Equal(4, settings.MonitorPoint);
    }
    [Fact]
    public void LegacyAndUnavailableModesFallBackSafely()
    {
        var legacy = new StudioSettings { Model = "RNNoise" }; legacy.Normalize();
        Assert.Equal(nameof(DenoiseMode.Eco), legacy.Model);
        var unavailable = new StudioSettings { Model = "Studio" }; unavailable.Normalize();
        Assert.Equal(nameof(DenoiseMode.Balanced), unavailable.Model);
    }
    [Fact]
    public void BothNativeModelsProduceFiniteAudioAndReduceStationaryNoise()
    {
        var random = new Random(18);
        foreach (IStudioDenoiser denoiser in new IStudioDenoiser[] { new RnNoiseDenoiser(), new DeepFilterDenoiser() })
        using (denoiser)
        {
            var frame = new float[480]; double before = 0, after = 0;
            for (int f = 0; f < 400; f++)
            {
                for (int i = 0; i < frame.Length; i++) { frame[i] = (float)(random.NextDouble() * .06 - .03); if (f > 100) before += frame[i] * frame[i]; }
                denoiser.Process(frame);
                Assert.All(frame, x => Assert.True(float.IsFinite(x)));
                if (f > 100) foreach (float x in frame) after += x * x;
            }
            Assert.True(after < before * .85, $"{denoiser.GetType().Name}: noise power {after} should be below {before * .85}");
        }
    }
    [Fact]
    public void GtcrnStreamingModelProcessesConsecutiveFrames()
    {
        using var denoiser = new GtcrnDenoiser();
        var frame = new float[480];
        for (int f = 0; f < 30; f++)
        {
            for (int i = 0; i < frame.Length; i++)
                frame[i] = .1f * (float)Math.Sin(2 * Math.PI * 440 * (f * 480 + i) / 48000);
            denoiser.Process(frame);
            Assert.All(frame, x => Assert.True(float.IsFinite(x)));
        }
    }
    [Fact]
    public void BalancedBenchmarkReportsMeasuredPercentiles()
    {
        var result = StudioBenchmark.Run(nameof(DenoiseMode.Balanced));
        Assert.True(double.IsFinite(result.Inference) && result.Inference > 0);
        Assert.True(result.Pipeline > 0 && result.P50 <= result.P95 && result.P95 <= result.Maximum);
        Assert.Equal(result.Pipeline / 10, result.Rtf, 8);
    }
    [Fact]
    public void MultiProcessPlayerSelectsTheAudiblePid()
    {
        const string path = @"C:\Program Files\Netease\CloudMusic\cloudmusic.exe";
        var settings = new StudioSettings { ApplicationPath = path, ApplicationPid = 24456 };
        var applications = new[]
        {
            new StudioApplication(24456, path, "CloudMusic", false, 0),
            new StudioApplication(40640, path, "CloudMusic", true, .013f)
        };
        Assert.Equal(40640, StudioController.ResolveApplication(settings, applications));
    }
    [Fact]
    public void EveryVisibleModeCanBeBenchmarked()
    {
        foreach (string mode in StudioDenoisers.Factories.Keys)
        {
            var result = StudioBenchmark.Run(mode);
            Assert.True(double.IsFinite(result.Pipeline) && result.Pipeline >= 0, mode);
            Assert.True(double.IsFinite(result.Rtf) && result.Rtf >= 0, mode);
        }
    }
}
