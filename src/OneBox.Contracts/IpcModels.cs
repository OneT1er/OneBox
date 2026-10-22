using System;
using System.Collections.Generic;
using System.Text.Json;
using System.Text.Json.Serialization;

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
    // Read-only: standby + modified page totals for the 「缓存」 figure.  Lets
    // the unprivileged GUI show Task Manager's Cached value without loading
    // the PerformanceCounter stack (added after protocol v1; older services
    // answer UnsupportedCommand and the GUI falls back locally).
    MemoryListStatus = 5,
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
            Result = JsonSerializer.SerializeToElement<object>(null, IpcJson.Options),
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

public sealed class MemoryListStatusResult
{
    // All standby priorities (Task Manager's "Standby" portion of Cached).
    public ulong StandbyBytes { get; set; }
    // Modified page list (dirty pages awaiting write-back).
    public ulong ModifiedBytes { get; set; }

    [JsonIgnore]
    public ulong CachedBytes => StandbyBytes + ModifiedBytes;
}

// Layout of SYSTEM_MEMORY_LIST_INFORMATION (NtQuerySystemInformation class
// 80): every field is a ULONG_PTR page count.  Kept here, free of P/Invoke,
// so both the privileged service and an elevated GUI decode it identically
// and the arithmetic is unit-testable.
public static class MemoryListLayout
{
    public const int InformationClass = 80;
    public const int ZeroPageIndex = 0;
    public const int FreePageIndex = 1;
    public const int ModifiedPageIndex = 2;
    public const int ModifiedNoWritePageIndex = 3;
    public const int BadPageIndex = 4;
    public const int StandbyByPriorityIndex = 5;
    public const int PriorityCount = 8;
    public const int RepurposedByPriorityIndex = StandbyByPriorityIndex + PriorityCount;
    public const int ModifiedPageFileIndex = RepurposedByPriorityIndex + PriorityCount;
    public const int FieldCount = ModifiedPageFileIndex + 1;

    public static MemoryListStatusResult FromPageCounts(ReadOnlySpan<ulong> fields, ulong pageSize)
    {
        if (fields.Length < FieldCount) throw new ArgumentException("Memory list information is truncated.", nameof(fields));
        if (pageSize == 0) throw new ArgumentOutOfRangeException(nameof(pageSize));
        ulong standbyPages = 0;
        for (int i = 0; i < PriorityCount; i++) standbyPages += fields[StandbyByPriorityIndex + i];
        return new MemoryListStatusResult
        {
            StandbyBytes = standbyPages * pageSize,
            ModifiedBytes = fields[ModifiedPageIndex] * pageSize,
        };
    }
}

public sealed class HardwareSubscribePayload
{
    public int MinimumIntervalMilliseconds { get; set; } = 500;
    // null preserves old clients; new clients send an explicit (possibly empty) selection.
    public List<HardwareSensorIdentity> SelectedSensors { get; set; }
    public bool IncludeCatalog { get; set; }
}

public sealed class HardwareSensorIdentity
{
    public string HardwareName { get; set; }
    public string SensorName { get; set; }
    public string SensorType { get; set; }
}

public sealed class HardwareSnapshot
{
    public bool CatalogIncluded { get; set; }
    public bool SelectionApplied { get; set; }
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
