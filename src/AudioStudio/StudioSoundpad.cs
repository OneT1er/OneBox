using System;
using System.IO;
using NAudio.Wave;
using NAudio.Wave.SampleProviders;

namespace PowerAudioManager.AudioStudio;

internal static class StudioSoundpadHotkeys
{
    public const int NativeIdBase = 0xBF90;
    public const int StopNativeId = 0xBF8F;
    public const int MaxEffects = 64;
    public const int StopEncoded = ((2 | 4) << 16) | 0x30; // Ctrl+Shift+0

    public static int LegacySlot(int index) => index < 9 ? ((2 | 4) << 16) | (0x31 + index) : 0;
    public static bool IsValid(int encoded)
    {
        if (encoded == 0) return true;
        int modifiers = (encoded >> 16) & 0xFFFF, key = encoded & 0xFFFF;
        return modifiers <= 15 && key > 0 && key <= 0xFF &&
            (modifiers != 0 || key >= 0x70 && key <= 0x87); // unmodified F1–F24 only
    }
}

internal sealed record StudioSoundClip(string Name, float[] Samples, string Id = "", float Gain = 1);

internal static class StudioSoundpadDecoder
{
    const int Rate = 48000;
    const int Channels = 2;
    const int MaxSeconds = 120;
    const int MaxSamples = Rate * Channels * MaxSeconds;

    public static StudioSoundClip Decode(string path)
    {
        if (!File.Exists(path)) throw new FileNotFoundException("音效文件不存在", path);
        using var reader = new AudioFileReader(path);
        if (reader.TotalTime.TotalSeconds > MaxSeconds)
            throw new InvalidOperationException("音效最长支持 2 分钟。请剪短后再导入。 ");
        ISampleProvider source = reader;
        if (source.WaveFormat.Channels == 1) source = new MonoToStereoSampleProvider(source);
        else if (source.WaveFormat.Channels != 2)
            throw new NotSupportedException("音效文件需要单声道或立体声。 ");
        if (source.WaveFormat.SampleRate != Rate) source = new WdlResamplingSampleProvider(source, Rate);
        int initial = Math.Clamp((int)Math.Ceiling(reader.TotalTime.TotalSeconds * Rate * Channels), 960, MaxSamples);
        var samples = new float[initial];
        var chunk = new float[4096];
        int count = 0;
        while (true)
        {
            int read = source.Read(chunk);
            if (read == 0) break;
            if (count + read > MaxSamples) throw new InvalidOperationException("音效最长支持 2 分钟。请剪短后再导入。 ");
            if (count + read > samples.Length) Array.Resize(ref samples, Math.Min(MaxSamples, Math.Max(count + read, samples.Length * 2)));
            for (int i = 0; i < read; i++) samples[count + i] = float.IsFinite(chunk[i]) ? Math.Clamp(chunk[i], -1f, 1f) : 0;
            count += read;
        }
        if (count == 0) throw new InvalidOperationException("音效文件没有可播放的音频。 ");
        Array.Resize(ref samples, count - count % Channels);
        return new StudioSoundClip(Path.GetFileNameWithoutExtension(path), samples);
    }
}

// Only the audio thread advances Position. UI actions atomically replace or stop
// the current clip, so the 10 ms real-time path never decodes or takes a lock.
internal sealed class StudioSoundpadPlayer
{
    sealed class Playback(StudioSoundClip clip)
    {
        public readonly StudioSoundClip Clip = clip;
        public int Position;
        public float Gain = clip.Gain;
    }

    Playback _current;
    public string PlayingName => System.Threading.Volatile.Read(ref _current)?.Clip.Name ?? "";
    public string PlayingId => System.Threading.Volatile.Read(ref _current)?.Clip.Id ?? "";
    public void Play(StudioSoundClip clip) => System.Threading.Volatile.Write(ref _current, new Playback(clip));
    public void Stop() => System.Threading.Volatile.Write(ref _current, null);
    public void UpdateGain(string id, float gain)
    {
        var playback = System.Threading.Volatile.Read(ref _current);
        if (playback?.Clip.Id == id) System.Threading.Volatile.Write(ref playback.Gain, gain);
    }
    public bool Read(float[] destination)
    {
        Array.Clear(destination);
        var playback = System.Threading.Volatile.Read(ref _current);
        if (playback == null) return false;
        int available = Math.Min(destination.Length, playback.Clip.Samples.Length - playback.Position);
        if (available <= 0)
        {
            System.Threading.Interlocked.CompareExchange(ref _current, null, playback);
            return false;
        }
        float gain = System.Threading.Volatile.Read(ref playback.Gain);
        for (int i = 0; i < available; i++) destination[i] = playback.Clip.Samples[playback.Position + i] * gain;
        playback.Position += available;
        if (playback.Position >= playback.Clip.Samples.Length)
            System.Threading.Interlocked.CompareExchange(ref _current, null, playback);
        return true;
    }
}
