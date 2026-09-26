using System;
using System.Text.Json;

namespace PowerAudioManager.AudioStudio;

internal sealed class StudioSettings
{
    public string InputId { get; set; } = "";
    public string OutputId { get; set; } = "";
    public string MonitorId { get; set; } = "";
    public string ApplicationPath { get; set; } = "";
    public string Model { get; set; } = "RNNoise";
    public string Preset { get; set; } = "Standard";
    public bool Denoise { get; set; } = true;
    public float Strength { get; set; } = .75f;
    public float MicGain { get; set; } = 1;
    public float MusicGain { get; set; } = .6f;
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
    public string Language { get; set; } = "zh";
    public string Theme { get; set; } = "light";
    public bool AutoStartAudio { get; set; }
    public bool AutoUpdate { get; set; }

    public StudioSettings Copy() => JsonSerializer.Deserialize<StudioSettings>(JsonSerializer.Serialize(this));
    public void Normalize()
    {
        Strength = Clamp(Strength, 0, 1); MicGain = Clamp(MicGain, 0, 3);
        MusicGain = Clamp(MusicGain, 0, 2); ExplodeStrength = Clamp(ExplodeStrength, .01f, 1);
        MonitorPoint = Math.Clamp(MonitorPoint, 0, 4);
        if (Bands == null || Bands.Length != 10) Bands = new float[10];
        for (int i = 0; i < 10; i++) Bands[i] = Clamp(Bands[i], -12, 12);
        if (CustomBands == null || CustomBands.Length != 10) CustomBands = new float[10];
        for (int i = 0; i < 10; i++) CustomBands[i] = Clamp(CustomBands[i], -12, 12);
        if (Model == null || !StudioDenoisers.Factories.ContainsKey(Model)) Model = "RNNoise";
        if (Language != "en") Language = "zh";
        if (Theme != "light") Theme = "dark";
    }
    static float Clamp(float v, float min, float max) => float.IsFinite(v) ? Math.Clamp(v, min, max) : min;
    public static StudioSettings Load()
    {
        try { var s = JsonSerializer.Deserialize<StudioSettings>(AppPrefs.GetString("AudioStudio.Settings", "{}")); s.Normalize(); s.Explode = false; return s; }
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
            "Quiet" => ("RNNoise", .35f, 1f),
            "Noisy" => ("DeepFilterNet3", 1f, 1f),
            "Live" => ("DeepFilterNet3", .85f, 1.15f),
            _ => ("RNNoise", .75f, 1f)
        };
        Denoise = true;
    }
}
