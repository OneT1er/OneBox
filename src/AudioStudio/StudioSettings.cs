using System;
using System.Collections.Generic;
using System.Text.Json;

namespace PowerAudioManager.AudioStudio;

internal sealed class StudioSoundEffect
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string Path { get; set; } = "";
    public float Gain { get; set; } = 1;
    public int Hotkey { get; set; }
}

internal sealed class StudioSettings
{
    public string InputId { get; set; } = "";
    public string OutputId { get; set; } = "";
    public string MonitorId { get; set; } = "";
    public string ApplicationPath { get; set; } = "";
    public int ApplicationPid { get; set; }
    public string Model { get; set; } = nameof(DenoiseMode.Balanced);
    public string Preset { get; set; } = "Standard";
    public bool Denoise { get; set; } = true;
    public float Strength { get; set; } = .75f;
    public float MicGain { get; set; } = 1;
    public float MusicGain { get; set; } = .6f;
    public float SoundpadGain { get; set; } = .8f;
    public List<StudioSoundEffect> SoundEffects { get; set; } = new();
    // Read once for settings written before per-effect volume and hotkeys existed.
    public List<string> SoundpadFiles { get; set; } = new();
    public bool Microphone { get; set; } = true;
    public bool Music { get; set; }
    public bool Eq { get; set; }
    public float[] Bands { get; set; } = new float[10];
    public float[] CustomBands { get; set; } = new float[10];
    public string EqPreset { get; set; } = "Flat";
    public bool Explode { get; set; }
    public float ExplodeStrength { get; set; } = .1f;
    public bool Monitor { get; set; }
    public int MonitorPoint { get; set; } = 4;
    public bool AutoStartAudio { get; set; }

    public StudioSettings Copy() => JsonSerializer.Deserialize<StudioSettings>(JsonSerializer.Serialize(this));
    public void Normalize()
    {
        Strength = Clamp(Strength, 0, 1); MicGain = Clamp(MicGain, 0, 3);
        MusicGain = Clamp(MusicGain, 0, 2); SoundpadGain = Clamp(SoundpadGain, 0, 2);
        SoundpadFiles ??= new List<string>();
        SoundEffects ??= new List<StudioSoundEffect>();
        if (SoundEffects.Count == 0)
            for (int i = 0; i < SoundpadFiles.Count && i < StudioSoundpadHotkeys.MaxEffects; i++)
                if (!string.IsNullOrWhiteSpace(SoundpadFiles[i]))
                    SoundEffects.Add(new StudioSoundEffect { Path = SoundpadFiles[i],
                        Hotkey = StudioSoundpadHotkeys.LegacySlot(i) });
        SoundpadFiles.Clear();
        SoundEffects.RemoveAll(x => x == null || string.IsNullOrWhiteSpace(x.Path));
        if (SoundEffects.Count > StudioSoundpadHotkeys.MaxEffects)
            SoundEffects.RemoveRange(StudioSoundpadHotkeys.MaxEffects,
                SoundEffects.Count - StudioSoundpadHotkeys.MaxEffects);
        var ids = new HashSet<string>(StringComparer.Ordinal);
        var hotkeys = new HashSet<int>();
        foreach (var effect in SoundEffects)
        {
            if (string.IsNullOrWhiteSpace(effect.Id) || !ids.Add(effect.Id))
            {
                effect.Id = Guid.NewGuid().ToString("N");
                ids.Add(effect.Id);
            }
            effect.Gain = Clamp(effect.Gain, 0, 2);
            if (!StudioSoundpadHotkeys.IsValid(effect.Hotkey) ||
                effect.Hotkey == StudioSoundpadHotkeys.StopEncoded ||
                (effect.Hotkey != 0 && !hotkeys.Add(effect.Hotkey))) effect.Hotkey = 0;
        }
        ExplodeStrength = Clamp(ExplodeStrength, .01f, 1);
        MonitorPoint = Math.Clamp(MonitorPoint, 0, 4);
        if (Bands == null || Bands.Length != 10) Bands = new float[10];
        for (int i = 0; i < 10; i++) Bands[i] = Clamp(Bands[i], -12, 12);
        if (CustomBands == null || CustomBands.Length != 10) CustomBands = new float[10];
        for (int i = 0; i < 10; i++) CustomBands[i] = Clamp(CustomBands[i], -12, 12);
        Model = Model switch
        {
            "RNNoise" => nameof(DenoiseMode.Eco),
            "GTCRN" => nameof(DenoiseMode.Balanced),
            "DeepFilterNet3" => nameof(DenoiseMode.Quality),
            _ when Model != null && StudioDenoisers.Factories.ContainsKey(Model) => Model,
            _ => nameof(DenoiseMode.Balanced)
        };
        if (Model == nameof(DenoiseMode.Off)) Denoise = false;
    }
    static float Clamp(float v, float min, float max) => float.IsFinite(v) ? Math.Clamp(v, min, max) : min;
    public static StudioSettings Load()
    {
        try
        {
            var s = JsonSerializer.Deserialize<StudioSettings>(AppPrefs.GetString("AudioStudio.Settings", "{}")) ?? new StudioSettings();
            string oldModel = s.Model;
            bool legacySounds = s.SoundpadFiles?.Count > 0;
            s.Normalize(); s.Explode = false;
            if (oldModel != s.Model || legacySounds) s.Save();
            return s;
        }
        catch { return new StudioSettings(); }
    }
    public bool Save()
    {
        var saved = Copy(); saved.Explode = false;
        return AppPrefs.SetString("AudioStudio.Settings", JsonSerializer.Serialize(saved));
    }
    public void ApplyPreset(string preset)
    {
        Preset = preset;
        (Model, Strength, MicGain) = preset switch
        {
            "Quiet" => (nameof(DenoiseMode.Eco), .35f, 1f),
            "Noisy" => (nameof(DenoiseMode.Quality), 1f, 1f),
            "Live" => (nameof(DenoiseMode.Quality), .85f, 1.15f),
            _ => (nameof(DenoiseMode.Balanced), .75f, 1f)
        };
        Denoise = true;
    }
}
