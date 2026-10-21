using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using OneBox.Contracts;
using PowerAudioManager;
using Xunit;

namespace OneBox.Tests;

public sealed class HardwareSnapshotResponseTests
{
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task TypedResponse_PreservesSnapshot(bool ready)
    {
        var request = IpcRequest.Create(IpcCommand.SubscribeHardware, new HardwareSubscribePayload());
        var snapshot = new HardwareSnapshot { Ready = ready, CpuTemperature = 52.5f };
        snapshot.Metrics.Add(new HardwareMetric { Key = "Temperature|CPU|Package", Name = "CPU", Value = 52.5f, Unit = "°C" });
        snapshot.Sensors.Add(new HardwareSensor { HardwareName = "CPU", SensorName = "Package", SensorType = "Temperature", HardwareType = "Cpu" });
        HardwareSnapshotResponse response = await Read(IpcResponse.Ok(request, snapshot, IpcCommand.HardwareSnapshot));
        HardwareSnapshot actual = response.ReadSnapshot(request.RequestId);
        Assert.Equal(ready, actual.Ready);
        Assert.Equal(snapshot.CpuTemperature, actual.CpuTemperature);
        Assert.Equal(snapshot.Metrics[0].Key, Assert.Single(actual.Metrics).Key);
        Assert.Equal(snapshot.Sensors[0].SensorName, Assert.Single(actual.Sensors).SensorName);
    }

    [Fact]
    public async Task TypedResponse_ExplicitNullIsNoSnapshot()
    {
        var request = IpcRequest.Create(IpcCommand.SubscribeHardware, new HardwareSubscribePayload());
        HardwareSnapshotResponse response = await Read(IpcResponse.Ok<HardwareSnapshot>(request, null, IpcCommand.HardwareSnapshot));
        Assert.True(response.HasResult);
        Assert.Null(response.ReadSnapshot(request.RequestId));
    }

    [Fact]
    public void TypedResponse_MissingResultFailsAfterEnvelopeValidation()
    {
        Guid id = Guid.NewGuid();
        var response = System.Text.Json.JsonSerializer.Deserialize<HardwareSnapshotResponse>(
            "{\"success\":true,\"requestId\":\"" + id + "\",\"command\":3}", IpcJson.Options);
        Assert.False(response.HasResult);
        Assert.Equal(IpcErrorCode.InvalidMessage, Assert.Throws<IpcProtocolException>(() => response.ReadSnapshot(id)).ErrorCode);
        response.Success = false;
        response.ErrorCode = IpcErrorCode.RateLimited;
        Assert.Equal(IpcErrorCode.RateLimited, Assert.Throws<IpcProtocolException>(() => response.ReadSnapshot(id)).ErrorCode);
    }

    [Fact]
    public async Task TypedResponse_PreservesErrorAndCorrelationChecks()
    {
        var request = IpcRequest.Create(IpcCommand.SubscribeHardware, new HardwareSubscribePayload());
        HardwareSnapshotResponse error = await Read(IpcResponse.Error(request, IpcErrorCode.ServiceUnavailable, "unavailable"));
        Assert.Equal(IpcErrorCode.ServiceUnavailable, Assert.Throws<IpcProtocolException>(() => error.ReadSnapshot(request.RequestId)).ErrorCode);
        HardwareSnapshotResponse success = await Read(IpcResponse.Ok(request, new HardwareSnapshot(), IpcCommand.HardwareSnapshot));
        Assert.Empty(success.ReadSnapshot(request.RequestId).Metrics);
        Assert.Equal(IpcErrorCode.InvalidMessage, Assert.Throws<IpcProtocolException>(() => success.ReadSnapshot(Guid.NewGuid())).ErrorCode);
        success.Version++;
        Assert.Equal(IpcErrorCode.InvalidMessage, Assert.Throws<IpcProtocolException>(() => success.ReadSnapshot(request.RequestId)).ErrorCode);
    }

    [Fact]
    public async Task TypedResponse_RejectsMalformedResultEvenInErrorEnvelope()
    {
        var request = IpcRequest.Create(IpcCommand.SubscribeHardware, new HardwareSubscribePayload());
        IpcResponse error = IpcResponse.Error(request, IpcErrorCode.ServiceUnavailable, "unavailable");
        error.Result = System.Text.Json.JsonSerializer.SerializeToElement(123);
        IpcProtocolException exception = await Assert.ThrowsAsync<IpcProtocolException>(() => Read(error));
        Assert.Equal(IpcErrorCode.InvalidMessage, exception.ErrorCode);
    }

    private static async Task<HardwareSnapshotResponse> Read(IpcResponse response)
    {
        using var stream = new MemoryStream();
        await IpcFraming.WriteAsync(stream, response, CancellationToken.None);
        stream.Position = 0;
        return await IpcFraming.ReadAsync<HardwareSnapshotResponse>(stream, CancellationToken.None);
    }
}
