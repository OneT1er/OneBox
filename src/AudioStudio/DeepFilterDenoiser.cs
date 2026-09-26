using System;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;

namespace PowerAudioManager.AudioStudio;

// The upstream LADSPA plugin owns a background inference thread. Keep its lifetime
// inside a dedicated child process; never unload a DLL with a live native thread.
internal sealed class DeepFilterDenoiser : IStudioDenoiser
{
    readonly Process _worker;
    readonly byte[] _bytes = new byte[StudioDsp.Frame * sizeof(float)];
    public double LastInferenceMilliseconds { get; private set; }
    public string MetricLabel => "Worker round-trip";
    public DeepFilterDenoiser()
    {
        string exe = Path.Combine(AppContext.BaseDirectory, "OneBox.exe");
        _worker = System.Diagnostics.Process.Start(new ProcessStartInfo(exe, "--audio-denoise-worker")
        {
            UseShellExecute = false, CreateNoWindow = true,
            RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true
        }) ?? throw new IOException("DeepFilterNet3 worker could not start.");
        _worker.ErrorDataReceived += (_, e) => { if (!string.IsNullOrWhiteSpace(e.Data)) AppLog.Log("DeepFilterNet3", e.Data); };
        _worker.BeginErrorReadLine();
        try
        {
            var ready = new byte[4]; Read(ready, 20000);
            if (BitConverter.ToInt32(ready) != 480) throw new IOException("Invalid DeepFilterNet3 handshake.");
        }
        catch { Dispose(); throw; }
    }
    public void Process(float[] frame, float strength = 1)
    {
        var clock = Stopwatch.StartNew();
        Buffer.BlockCopy(frame, 0, _bytes, 0, _bytes.Length);
        _worker.StandardInput.BaseStream.Write(BitConverter.GetBytes(Math.Clamp(strength, 0, 1)));
        _worker.StandardInput.BaseStream.Write(_bytes);
        _worker.StandardInput.BaseStream.Flush();
        Read(_bytes, 2000);
        LastInferenceMilliseconds = clock.Elapsed.TotalMilliseconds;
        Buffer.BlockCopy(_bytes, 0, frame, 0, _bytes.Length);
    }
    void Read(byte[] bytes, int timeout)
    {
        using var cancel = new CancellationTokenSource(timeout);
        _worker.StandardOutput.BaseStream.ReadExactlyAsync(bytes, cancel.Token).AsTask().GetAwaiter().GetResult();
    }
    public void Dispose()
    {
        try { if (!_worker.HasExited) _worker.Kill(true); } catch { }
        _worker.Dispose();
    }

    [StructLayout(LayoutKind.Sequential)]
    struct Descriptor
    {
        public uint Id; public IntPtr Label; public uint Properties;
        public IntPtr Name, Maker, Copyright; public uint PortCount;
        public IntPtr PortDescriptors, PortNames, PortHints, Implementation;
        public IntPtr Instantiate, Connect, Activate, Run, RunAdding, Gain, Deactivate, Cleanup;
    }
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] delegate IntPtr GetDescriptor(uint index);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] delegate IntPtr Instantiate(IntPtr descriptor, uint rate);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] delegate void Connect(IntPtr instance, uint port, IntPtr data);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] delegate void Run(IntPtr instance, uint samples);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] delegate void Activate(IntPtr instance);

    public static int WorkerMain()
    {
        try
        {
            IntPtr library = NativeLibrary.Load(Path.Combine(AppContext.BaseDirectory, "AudioStudio", "Native", "deep_filter_ladspa.dll"));
            var get = Marshal.GetDelegateForFunctionPointer<GetDescriptor>(NativeLibrary.GetExport(library, "ladspa_descriptor"));
            IntPtr address = get(0);
            var descriptor = Marshal.PtrToStructure<Descriptor>(address);
            if (descriptor.PortCount != 8 || Marshal.PtrToStringAnsi(descriptor.Label) != "deep_filter_mono") return 2;
            var create = Marshal.GetDelegateForFunctionPointer<Instantiate>(descriptor.Instantiate);
            var connect = Marshal.GetDelegateForFunctionPointer<Connect>(descriptor.Connect);
            var run = Marshal.GetDelegateForFunctionPointer<Run>(descriptor.Run);
            IntPtr instance = create(address, 48000);
            if (instance == IntPtr.Zero) return 3;
            // Public LADSPA port order, as documented by the pinned upstream release.
            IntPtr input = Marshal.AllocHGlobal(1920), output = Marshal.AllocHGlobal(1920);
            connect(instance, 0, input); connect(instance, 1, output);
            float[] controls = { 100, -15, 35, 35, 1, 0 };
            IntPtr attenuation = IntPtr.Zero;
            for (uint i = 0; i < controls.Length; i++)
            {
                IntPtr control = Marshal.AllocHGlobal(4);
                Marshal.Copy(new[] { controls[i] }, 0, control, 1);
                connect(instance, i + 2, control);
                if (i == 0) attenuation = control;
            }
            if (descriptor.Activate != IntPtr.Zero) Marshal.GetDelegateForFunctionPointer<Activate>(descriptor.Activate)(instance);
            using var stdin = Console.OpenStandardInput(); using var stdout = Console.OpenStandardOutput();
            stdout.Write(BitConverter.GetBytes(480)); stdout.Flush();
            byte[] data = new byte[1920], strengthBytes = new byte[4];
            while (true)
            {
                try { stdin.ReadExactly(strengthBytes); stdin.ReadExactly(data); } catch (EndOfStreamException) { return 0; }
                float strength = BitConverter.ToSingle(strengthBytes);
                if (!float.IsFinite(strength)) return 4;
                Marshal.Copy(new[] { Math.Clamp(strength, 0, 1) * 100 }, 0, attenuation, 1);
                Marshal.Copy(data, 0, input, data.Length);
                run(instance, 480);
                Marshal.Copy(output, data, 0, data.Length);
                stdout.Write(data); stdout.Flush();
            }
        }
        catch (Exception e) { Console.Error.WriteLine(e.Message); return 1; }
    }
}
