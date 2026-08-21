using System;

namespace OneBox.Contracts;

public readonly record struct IpcValidationResult(bool IsValid, IpcErrorCode ErrorCode, string ErrorMessage)
{
    public static IpcValidationResult Valid => new(true, IpcErrorCode.None, string.Empty);
}

public static class IpcValidator
{
    public static IpcValidationResult Validate(IpcRequest request, params IpcCommand[] allowedCommands)
    {
        if (request == null)
            return new(false, IpcErrorCode.InvalidMessage, "Request is missing.");
        if (request.Version != IpcProtocol.Version)
            return new(false, IpcErrorCode.UnsupportedVersion, "Unsupported protocol version.");
        if (request.RequestId == Guid.Empty)
            return new(false, IpcErrorCode.InvalidRequestId, "Request id is required.");
        if (!Enum.IsDefined(request.Command) || request.Command == IpcCommand.None)
            return new(false, IpcErrorCode.UnsupportedCommand, "Unknown command.");
        if (allowedCommands is { Length: > 0 } && Array.IndexOf(allowedCommands, request.Command) < 0)
            return new(false, IpcErrorCode.UnsupportedCommand, "Command is not allowed on this endpoint.");
        return IpcValidationResult.Valid;
    }
}
