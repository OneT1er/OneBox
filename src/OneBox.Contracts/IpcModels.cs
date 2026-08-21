using System;
using System.Collections.Generic;
using System.Text.Json;

namespace OneBox.Contracts;

public static class IpcProtocol
{
    public const int Version = 1;
    public const int MaxMessageBytes = 256 * 1024;
    public const int MaxConcurrentConnections = 4;
    public const int MaxRequestsPerSecond = 8;
    public static readonly TimeSpan ConnectTimeout = TimeSpan.FromSeconds(8);
    public static readonly TimeSpan ReadTimeout = TimeSpan.FromSeconds(15);
    public static readonly TimeSpan WriteTimeout = TimeSpan.FromSeconds(5);
}

public enum IpcCommand
{
    None = 0,
    CleanMemory = 1,
    SubscribeHardware = 2,
    HardwareSnapshot = 3,
    Ping = 4,
}

public enum IpcErrorCode
{
    None = 0,
    InvalidMessage = 1,
    UnsupportedVersion = 2,
    InvalidRequestId = 3,
    UnsupportedCommand = 4,
    PayloadTooLarge = 5,
    InvalidPayload = 6,
    Unauthorized = 7,
    RateLimited = 8,
    Timeout = 9,
    InternalError = 10,
    ServiceUnavailable = 11,
}

public sealed class IpcRequest
{
    public int Version { get; set; } = IpcProtocol.Version;
    public Guid RequestId { get; set; }
    public IpcCommand Command { get; set; }
    public JsonElement Payload { get; set; }

    public static IpcRequest Create<TPayload>(IpcCommand command, TPayload payload, Guid? requestId = null)
    {
        return new IpcRequest
        {
            Version = IpcProtocol.Version,
            RequestId = requestId ?? Guid.NewGuid(),
            Command = command,
            Payload = JsonSerializer.SerializeToElement(payload, IpcJson.Options),
        };
    }
}

public sealed class IpcResponse
{
    public int Version { get; set; } = IpcProtocol.Version;
    public Guid RequestId { get; set; }
    public IpcCommand Command { get; set; }
    public bool Success { get; set; }
    public IpcErrorCode ErrorCode { get; set; }
    public string ErrorMessage { get; set; }
    public JsonElement Result { get; set; }

    public static IpcResponse Ok<T>(IpcRequest request, T result, IpcCommand? command = null)
    {
        return new IpcResponse
        {
            RequestId = request.RequestId,
            Command = command ?? request.Command,
            Success = true,
            ErrorCode = IpcErrorCode.None,
            Result = JsonSerializer.SerializeToElement(result, IpcJson.Options),
        };
    }

    public static IpcResponse Error(IpcRequest request, IpcErrorCode code, string message)
    {
        return new IpcResponse
        {
            RequestId = request?.RequestId ?? Guid.Empty,
            Command = request?.Command ?? IpcCommand.None,
            Success = false,
            ErrorCode = code,
            ErrorMessage = message ?? string.Empty,
        };
    }

    public T ReadResult<T>() => Result.Deserialize<T>(IpcJson.Options);
}

public sealed class CleanMemoryPayload
{
    public int Flags { get; set; }
}

public sealed class CleanMemoryResult
{
    public ulong FreedBytes { get; set; }
}

public sealed class HardwareSubscribePayload
{
    public int MinimumIntervalMilliseconds { get; set; } = 500;
}

public sealed class HardwareSnapshot
{
    public float? CpuTemperature { get; set; }
    public float? GpuTemperature { get; set; }
    public bool Ready { get; set; }
    public List<HardwareMetric> Metrics { get; set; } = new();
    public List<HardwareSensor> Sensors { get; set; } = new();
}

public sealed class HardwareMetric
{
    public string Name { get; set; }
    public string Icon { get; set; }
    public float? Value { get; set; }
    public string Unit { get; set; }
    public string Key { get; set; }
}

public sealed class HardwareSensor
{
    public string HardwareName { get; set; }
    public string SensorName { get; set; }
    public string HardwareType { get; set; }
    public string SensorType { get; set; }
}

public static class IpcJson
{
    public static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web)
    {
        PropertyNameCaseInsensitive = false,
        MaxDepth = 32,
    };
}
