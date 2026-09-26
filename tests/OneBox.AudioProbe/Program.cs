using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using NAudio.CoreAudioApi;
using NAudio.Wave;
using PowerAudioManager;
using PowerAudioManager.AudioStudio;

internal static class Program
{
    [STAThread]
    static int Main(string[] args)
    {
        try
        {
            if (args[0] == "preview") { Preview(args[1]); return 0; }
            if (args[0] == "tone")
            {
                using var devices = new MMDeviceEnumerator(); using var device = devices.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia);
                using var player = new WasapiPlayerBuilder().WithDevice(device).WithSharedMode().Build();
                player.Init(new Tone(float.Parse(args[1], System.Globalization.CultureInfo.InvariantCulture))); player.Play();
                Console.WriteLine("READY"); Console.ReadLine(); return 0;
            }
            Route().GetAwaiter().GetResult(); return 0;
        }
        catch (Exception ex) { Console.Error.WriteLine(ex); return 1; }
    }
    static async Task Route()
    {
        var outputs = StudioDevices.List(DataFlow.Render); var inputs = StudioDevices.List(DataFlow.Capture);
        Console.WriteLine("Endpoints: " + string.Join("; ", outputs.Select(x => x.Name)));
        var cable = outputs.Single(x => x.Name.Contains("CABLE Input", StringComparison.OrdinalIgnoreCase));
        var recording = inputs.Single(x => x.Name.Contains("CABLE Output", StringComparison.OrdinalIgnoreCase));
        using var first = StartTone(440); using var second = StartTone(880);
        try
        {
            using var enumerator = new MMDeviceEnumerator(); using var endpoint = enumerator.GetDevice(recording.Id);
            using var recorder = new WasapiRecorderBuilder().WithDevice(endpoint).WithSharedMode().WithFormat(WaveFormat.CreateIeeeFloatWaveFormat(48000, 2)).Build();
            var captured = new List<float>(); object gate = new();
            recorder.DataAvailable += (data, flags, _, _) => { if ((flags & AudioClientBufferFlags.Silent) == 0) lock (gate) captured.AddRange(MemoryMarshal.Cast<byte, float>(data).ToArray()); };
            recorder.StartRecording();
            using var engine = new StudioEngine();
            await engine.StartAsync(new StudioSettings { Microphone = false, Denoise = false, Music = true, MusicGain = 1, OutputId = cable.Id }, first.Id);
            await Task.Delay(700);
            lock (gate) captured.Clear();
            await Task.Delay(1700);
            float[] samples; lock (gate) samples = captured.ToArray();
            if (engine.Error != null) throw new Exception(engine.Error);
            double wanted = Amplitude(samples, 440), excluded = Amplitude(samples, 880);
            Console.WriteLine($"CABLE microphone: samples={samples.Length}, selected 440Hz={wanted:F6}, excluded 880Hz={excluded:F6}");
            if (wanted < .00005 || excluded > wanted * .15) throw new Exception("Per-process isolation failed");
            engine.Update(new StudioSettings { Microphone = false, Denoise = false, Music = false, OutputId = cable.Id });
            await Task.Delay(500); lock (gate) captured.Clear(); await Task.Delay(600);
            lock (gate) samples = captured.ToArray();
            if (samples.Length > 0 && samples.Max(Math.Abs) > .00005) throw new Exception("BGM stop leaked audio");
            Console.WriteLine("PASS: selected-process capture → stereo mix → limiter → CABLE Output; BGM off is silent.");
            var virtualSink = outputs.FirstOrDefault(x => x.Id != cable.Id && x.IsCable);
            if (virtualSink != null)
            {
                using var toneDevice = enumerator.GetDevice(cable.Id);
                using var tone = new WasapiPlayerBuilder().WithDevice(toneDevice).WithSharedMode().Build();
                tone.Init(new Tone(330)); tone.Play();
                using var microphoneEngine = new StudioEngine();
                await microphoneEngine.StartAsync(new StudioSettings { InputId = recording.Id, OutputId = virtualSink.Id, Microphone = true, Denoise = false, Music = false }, 0);
                await Task.Delay(700);
                if (microphoneEngine.Error != null || microphoneEngine.InputPeak < .0001 || microphoneEngine.OutputPeak < .0001)
                    throw new Exception("Shared-mode microphone capture failed: " + microphoneEngine.Error);
                Console.WriteLine($"PASS: WASAPI Shared microphone path using synthetic CABLE input; input={microphoneEngine.InputPeak:F6}, output={microphoneEngine.OutputPeak:F6}");
            }
            // Verify PnP-based USB identity without opening the real microphone.
            foreach (var input in inputs)
            foreach (var output in outputs)
                if (StudioDevices.SameUsbDevice(input.Id, output.Id)) Console.WriteLine("Shared USB detected: " + input.Name + " / " + output.Name);
        }
        finally { first.StandardInput.Close(); second.StandardInput.Close(); if (!first.WaitForExit(3000)) first.Kill(); if (!second.WaitForExit(3000)) second.Kill(); }
    }
    static Process StartTone(int frequency)
    {
        var process = Process.Start(new ProcessStartInfo(Environment.ProcessPath, "tone " + frequency) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardInput = true, RedirectStandardOutput = true });
        if (process.StandardOutput.ReadLine() != "READY") throw new Exception("Tone process failed"); return process;
    }
    static double Amplitude(float[] stereo, double frequency)
    {
        double sin = 0, cos = 0; int n = stereo.Length / 2;
        for (int i = 0; i < n; i++) { double phase = 2 * Math.PI * frequency * i / 48000; sin += stereo[i * 2] * Math.Sin(phase); cos += stereo[i * 2] * Math.Cos(phase); }
        return n == 0 ? 0 : 2 * Math.Sqrt(sin * sin + cos * cos) / n;
    }
    sealed class Tone(float frequency) : IWaveProvider
    {
        long _position;
        public WaveFormat WaveFormat { get; } = WaveFormat.CreateIeeeFloatWaveFormat(48000, 2);
        public int Read(byte[] buffer, int offset, int count)
            => Read(buffer.AsSpan(offset, count));
        public int Read(Span<byte> buffer)
        {
            var samples = MemoryMarshal.Cast<byte, float>(buffer);
            for (int i = 0; i < samples.Length; i += 2) { float value = .002f * MathF.Sin((float)(2 * Math.PI * frequency * _position++ / 48000)); samples[i] = value; samples[i + 1] = value * .5f; }
            return buffer.Length;
        }
    }
    static void Preview(string directory)
    {
        Directory.CreateDirectory(directory);
        var application = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        ThemeTokens.Apply(application);
        using var controller = new StudioController();
        foreach (string theme in new[] { "light", "dark", "english" })
        {
            controller.Settings.Theme = theme == "english" ? "dark" : theme;
            controller.Settings.Language = theme == "english" ? "en" : "zh";
            var view = new StudioWindow(null, controller);
            var field = typeof(StudioWindow).GetField("_window", BindingFlags.Instance | BindingFlags.NonPublic);
            var window = (Window)field.GetValue(view);
            window.WindowStartupLocation = WindowStartupLocation.Manual; window.Left = -10000; window.Top = -10000;
            window.Show(); Pump();
            Render(window, Path.Combine(directory, "studio-" + theme + ".png"));
            foreach (string method in new[] { "Equalizer", "Settings", "Guide" })
            {
                typeof(StudioWindow).GetMethod(method, BindingFlags.Instance | BindingFlags.NonPublic).Invoke(view, null);
                Pump(); var children = window.OwnedWindows.Cast<Window>().ToArray();
                foreach (Window child in children) { Render(child, Path.Combine(directory, method.ToLowerInvariant() + "-" + theme + ".png")); child.Close(); }
            }
            view.Close();
        }
        application.Shutdown(); Console.WriteLine("Rendered UI previews to " + directory);
    }
    static void Pump()
    {
        var frame = new DispatcherFrame(); var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(300) };
        timer.Tick += (_, _) => { timer.Stop(); frame.Continue = false; }; timer.Start(); Dispatcher.PushFrame(frame);
    }
    static void Render(Window window, string path)
    {
        window.UpdateLayout();
        var bitmap = new RenderTargetBitmap((int)window.ActualWidth, (int)window.ActualHeight, 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(window); var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var stream = File.Create(path); encoder.Save(stream);
    }
}
