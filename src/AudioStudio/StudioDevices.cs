using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using NAudio.CoreAudioApi;

namespace PowerAudioManager.AudioStudio;

internal sealed record StudioDevice(string Id, string Name)
{
    public override string ToString() => Name;
    public bool IsCable => Name.Contains("CABLE", StringComparison.OrdinalIgnoreCase) || Name.Contains("Virtual", StringComparison.OrdinalIgnoreCase);
}
internal sealed record StudioApplication(int Pid, string Path, string Name)
{
    public override string ToString() => Name;
}
internal static class StudioDevices
{
    public static string DefaultMicrophone()
    {
        try
        {
            using var enumerator = new MMDeviceEnumerator();
            using var device = enumerator.GetDefaultAudioEndpoint(DataFlow.Capture, Role.Communications);
            if (!new StudioDevice(device.ID, device.FriendlyName).IsCable) return device.ID;
        }
        catch { }
        return List(DataFlow.Capture).FirstOrDefault(x => !x.IsCable)?.Id ?? "";
    }
    public static StudioDevice[] List(DataFlow flow)
    {
        using var e = new MMDeviceEnumerator();
        var result = new List<StudioDevice>();
        foreach (var device in e.EnumerateAudioEndPoints(flow, DeviceState.Active))
            using (device) result.Add(new(device.ID, device.FriendlyName));
        return result.ToArray();
    }
    public static StudioApplication[] Applications()
    {
        using var e = new MMDeviceEnumerator();
        var result = new Dictionary<int, StudioApplication>();
        foreach (var device in e.EnumerateAudioEndPoints(DataFlow.Render, DeviceState.Active))
        using (device)
        {
            try
            {
                var sessions = device.AudioSessionManager.Sessions;
                for (int i = 0; i < sessions.Count; i++)
                {
                    using var session = sessions[i];
                    int pid = (int)session.GetProcessID;
                    if (pid == 0 || pid == Environment.ProcessId || result.ContainsKey(pid)) continue;
                    string path = ProcessPath(pid);
                    if (string.IsNullOrEmpty(path)) continue;
                    string name = System.IO.Path.GetFileNameWithoutExtension(path);
                    try { name = FileVersionInfo.GetVersionInfo(path).FileDescription ?? name; } catch { }
                    result[pid] = new(pid, path, name + " · " + pid);
                }
            }
            catch (Exception ex) { AppLog.Log("AudioStudio sessions", ex.Message); }
        }
        return result.Values.OrderBy(x => x.Name).ToArray();
    }
    public static string ProcessPath(int pid)
    {
        IntPtr handle = OpenProcess(0x1000, false, pid);
        if (handle == IntPtr.Zero) return "";
        try { var buffer = new StringBuilder(32768); int size = buffer.Capacity; return QueryFullProcessImageName(handle, 0, buffer, ref size) ? buffer.ToString() : ""; }
        finally { CloseHandle(handle); }
    }
    public static bool SameUsbDevice(string first, string second)
    {
        if (string.IsNullOrEmpty(first) || string.IsNullOrEmpty(second)) return false;
        var a = EndpointIdentity(first); var b = EndpointIdentity(second);
        return a.Container != Guid.Empty && a.Container == b.Container && a.Parent != null && a.Parent.StartsWith("USB\\", StringComparison.OrdinalIgnoreCase);
    }
    static (Guid Container, string Parent) EndpointIdentity(string endpoint)
    {
        if (CM_Locate_DevNode(out uint node, "SWD\\MMDEVAPI\\" + endpoint, 0) != 0) return default;
        var containerKey = new DevPropertyKey { Format = new Guid("8c7ed206-3f8a-4827-b3ab-ae9e1faefc6c"), Id = 2 };
        var parentKey = new DevPropertyKey { Format = new Guid("4340a6c5-93fa-4706-972c-7b648008a5a7"), Id = 8 };
        byte[] container = new byte[16], parent = new byte[2048]; uint size = 16;
        if (CM_Get_DevNode_Property(node, ref containerKey, out _, container, ref size, 0) != 0) return default;
        size = (uint)parent.Length;
        if (CM_Get_DevNode_Property(node, ref parentKey, out _, parent, ref size, 0) != 0) return default;
        return (new Guid(container), Encoding.Unicode.GetString(parent, 0, (int)size).TrimEnd('\0'));
    }
    [StructLayout(LayoutKind.Sequential)] struct DevPropertyKey { public Guid Format; public uint Id; }
    [DllImport("cfgmgr32.dll", CharSet = CharSet.Unicode, EntryPoint = "CM_Locate_DevNodeW")]
    static extern uint CM_Locate_DevNode(out uint node, string id, uint flags);
    [DllImport("cfgmgr32.dll", CharSet = CharSet.Unicode, EntryPoint = "CM_Get_DevNode_PropertyW")]
    static extern uint CM_Get_DevNode_Property(uint node, ref DevPropertyKey key, out uint type, byte[] data, ref uint size, uint flags);
    [DllImport("kernel32.dll")] static extern IntPtr OpenProcess(uint access, bool inherit, int pid);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] static extern bool QueryFullProcessImageName(IntPtr process, uint flags, StringBuilder path, ref int size);
    [DllImport("kernel32.dll")] static extern bool CloseHandle(IntPtr handle);
}
