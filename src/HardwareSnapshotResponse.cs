using System;
using System.Text.Json.Serialization;
using OneBox.Contracts;

namespace PowerAudioManager;

// GUI-only view of the existing wire envelope avoids materializing Result as
// a JsonElement before deserializing the same hardware snapshot again.
internal sealed class HardwareSnapshotResponse
{
    public int Version { get; set; } = IpcProtocol.Version;
    public Guid RequestId { get; set; }
    public IpcCommand Command { get; set; }
    public bool Success { get; set; }
    public IpcErrorCode ErrorCode { get; set; }
    public string ErrorMessage { get; set; }

    private HardwareSnapshot _result;
    public HardwareSnapshot Result
    {
        get => _result;
        set { _result = value; HasResult = true; }
    }

    [JsonIgnore]
    public bool HasResult { get; private set; }

    internal HardwareSnapshot ReadSnapshot(Guid requestId)
    {
        if (!Success)
            throw new IpcProtocolException(ErrorCode, ErrorMessage ?? "Hardware helper rejected the request.");
        if (Version != IpcProtocol.Version || RequestId != requestId || Command != IpcCommand.HardwareSnapshot)
            throw new IpcProtocolException(IpcErrorCode.InvalidMessage, "Hardware response envelope is invalid.");
        if (!HasResult)
            throw new IpcProtocolException(IpcErrorCode.InvalidMessage, "Hardware response result is missing.");
        return Result;
    }
}
