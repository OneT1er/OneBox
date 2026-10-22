using System;
using System.Collections;
using System.Linq;
using System.IO.Pipes;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using OneBox.Contracts;
using PowerAudioManager;
using Xunit;

namespace OneBox.Tests;

[Collection("PerfHistory")]
public sealed class HardwareConnectionTests
{
    [Fact]
    public async Task StoppedRunCannotReplaceTheNewRunsPipe()
    {
        var service = new HardwareMonitorService(() => "");
        using var currentPipe = new NamedPipeClientStream(".", "OneBox.Tests.Unconnected", PipeDirection.InOut);
        ServiceField("_pipeGeneration").SetValue(service, 2);
        ServiceField("_activePipe").SetValue(service, currentPipe);
        var oldRun = (Task)typeof(HardwareMonitorService).GetMethod("RunPipeClientAsync", BindingFlags.Instance | BindingFlags.NonPublic)
            .Invoke(service, new object[] { CancellationToken.None, 1 });
        await oldRun;
        Assert.Same(currentPipe, ServiceField("_activePipe").GetValue(service));
    }

    [Fact]
    public async Task OldStopCleanupDoesNotClearAStartedGenerationsSnapshot()
    {
        const string key = "Temperature|ConnectionTest|Package|Connection test|cpu";
        var service = new HardwareMonitorService(() => key);
        service.PrepareSelection();
        var oldTask = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        ServiceField("_started").SetValue(service, true);
        ServiceField("_pipeTask").SetValue(service, oldTask.Task);
        object gate = ServiceField("_gate").GetValue(service);
        Task stopping = Task.Run(() => service.Stop(), TestContext.Current.CancellationToken);
        Assert.True(SpinWait.SpinUntil(() => { lock (gate) return !(bool)ServiceField("_started").GetValue(service); }, 1000));
        try
        {
            lock (gate)
            {
                // Reproduce the state transition of Start while the old Stop waits.
                ServiceField("_pipeGeneration").SetValue(service, (int)ServiceField("_pipeGeneration").GetValue(service) + 1);
                ServiceField("_started").SetValue(service, true);
                service.CaptureSubscription(out int revision);
                var snapshot = new HardwareSnapshot { Ready = true, SelectionApplied = true, CpuTemperature = 51 };
                snapshot.Metrics.Add(new HardwareMetric { Key = key, Value = 51, Unit = "°C" });
                Assert.True(service.ApplySnapshotIfCurrent(snapshot, revision));
            }
        }
        finally { oldTask.SetResult(); await stopping; }
        Assert.True(service.IsAvailable);
        Assert.Equal(51, Assert.Single(service.ActiveMetrics).Value);
        Assert.Equal(51, service.CpuTemperature);
    }

    [Fact]
    public void DisconnectStopsHistoryAndNewSnapshotResumesIt()
    {
        const string key = "Temperature|ConnectionTest|Package|Connection test|cpu";
        var service = new HardwareMonitorService(() => key);
        service.PrepareSelection();
        service.CaptureSubscription(out int revision);
        var snapshot = new HardwareSnapshot { Ready = true, SelectionApplied = true, CpuTemperature = 42, GpuTemperature = 43 };
        snapshot.Metrics.Add(new HardwareMetric { Key = key, Value = 42, Unit = "°C" });
        FieldInfo loaded = Field("_loaded");
        var series = (IDictionary)Field("_series").GetValue(null);
        lock (Field("_saveIoLock").GetValue(null))
        lock (Field("_lock").GetValue(null))
        {
            object previousLoaded = loaded.GetValue(null);
            try
            {
                // Isolate the in-memory history test from user files and its save timer.
                loaded.SetValue(null, true);
                Assert.True(service.ApplySnapshotIfCurrent(snapshot, revision));
                PerfHistory.Add(service.ActiveMetrics);
                Assert.Equal(1, PointCount());

                service.InvalidateSnapshot();
                Assert.Null(service.CpuTemperature);
                Assert.Null(service.GpuTemperature);
                for (int tick = 0; tick < 3; tick++)
                {
                    service.Update();
                    Assert.False(service.IsAvailable);
                    Assert.Empty(service.ActiveMetrics);
                    PerfHistory.Add(service.ActiveMetrics);
                }
                Assert.Equal(1, PointCount());

                snapshot.Metrics[0].Value = 43;
                Assert.True(service.ApplySnapshotIfCurrent(snapshot, revision));
                service.Update();
                Assert.False(Assert.Single(service.ActiveMetrics).Cached);
                PerfHistory.Add(service.ActiveMetrics);
                Assert.Equal(2, PointCount());

                snapshot.Ready = false;
                Assert.True(service.ApplySnapshotIfCurrent(snapshot, revision));
                service.Update();
                Assert.Empty(service.ActiveMetrics);
                PerfHistory.Add(service.ActiveMetrics);
                Assert.Equal(2, PointCount());
            }
            finally { series.Remove(key); loaded.SetValue(null, previousLoaded); }
        }

        static int PointCount() => PerfHistory.GetSeries(DateTime.MinValue, DateTime.MaxValue)
            .Single(item => item.Name == "Connection test").Points.Count;
    }

    private static FieldInfo Field(string name) => typeof(PerfHistory).GetField(name, BindingFlags.NonPublic | BindingFlags.Static);
    private static FieldInfo ServiceField(string name) => typeof(HardwareMonitorService).GetField(name, BindingFlags.NonPublic | BindingFlags.Instance);
}
