using System;
using System.IO.Pipes;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Threading;

namespace PowerAudioManager
{
    /// <summary>
    /// admin 温度 helper 进程（OneBox.exe --temp-monitor）：运行 LibreHardwareMonitor 读温度，
    /// 通过命名管道 OneBox\TempMonitor 每秒推送 JSON 给普通权限的主 OneBox。
    /// 60s 无客户端连接则退出（主 OneBox 退出后自动清理）。
    /// </summary>
    public static class TempMonitorHelper
    {
        public static void Run()
        {
            try
            {
                HardwareMonitorService.Instance.Start();
                AppLog.Log("TempHelper", "hw starting, admin=" + AdminUtils.IsAdmin());

                var security = new PipeSecurity();
                security.AddAccessRule(new PipeAccessRule(new System.Security.Principal.SecurityIdentifier(System.Security.Principal.WellKnownSidType.WorldSid, null), PipeAccessRights.ReadWrite, System.Security.AccessControl.AccessControlType.Allow));
                using (var server = NamedPipeServerStreamAcl.Create("Global\\OneBox\\TempMonitor", PipeDirection.Out, 1, PipeTransmissionMode.Byte, PipeOptions.None, 0, 0, security))
                {
                    while (true)
                    {
                        try
                        {
                            using var cts = new CancellationTokenSource(60000);
                            server.WaitForConnectionAsync(cts.Token).GetAwaiter().GetResult();
                        }
                        catch (OperationCanceledException)
                        {
                            AppLog.Log("TempHelper", "no client 60s, exit");
                            break;
                        }
                        try
                        {
                            while (server.IsConnected)
                            {
                                HardwareMonitorService.Instance.Update();
                                var payload = new
                                {
                                    cpu = HardwareMonitorService.Instance.CpuTemperature,
                                    gpu = HardwareMonitorService.Instance.GpuTemperature,
                                    ready = HardwareMonitorService.Instance.IsAvailable,
                                    allMetrics = HardwareMonitorService.Instance.ReadAllMetrics().Select(m => new
                                    {
                                        name = m.DisplayName,
                                        icon = m.IconKey,
                                        value = m.Value,
                                        unit = m.Unit,
                                        key = m.ConfigKey
                                    }).ToArray(),
                                    sensors = HardwareMonitorService.Instance.AllTempSensors.Count == 0 ? null :
                                              HardwareMonitorService.Instance.AllTempSensors.Select(s => new { hw = s.HardwareName, name = s.SensorName, hwtype = s.HwType.ToString(), stype = s.SensorType.ToString() }).ToArray(),
                                    fans = HardwareMonitorService.Instance.AllFanSensors.Count == 0 ? null :
                                           HardwareMonitorService.Instance.AllFanSensors.Select(s => new { hw = s.HardwareName, name = s.SensorName, hwtype = s.HwType.ToString(), stype = s.SensorType.ToString() }).ToArray(),
                                    controls = HardwareMonitorService.Instance.AllControlSensors.Count == 0 ? null :
                                               HardwareMonitorService.Instance.AllControlSensors.Select(s => new { hw = s.HardwareName, name = s.SensorName, hwtype = s.HwType.ToString(), stype = s.SensorType.ToString() }).ToArray()
                                };
                                byte[] bytes = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(payload) + "\n");
                                try { server.Write(bytes, 0, bytes.Length); server.Flush(); }
                                catch { break; }
                                Thread.Sleep(500);
                            }
                        }
                        catch (Exception ex) { AppLog.Log("TempHelper", "conn err: " + ex.Message); }
                        try { server.Disconnect(); } catch { }
                    }
                }
            }
            catch (Exception ex) { AppLog.Log("TempHelper", "fatal: " + ex.Message); }
        }
    }
}
