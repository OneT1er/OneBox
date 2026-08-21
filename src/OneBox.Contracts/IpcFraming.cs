using System;
using System.Buffers.Binary;
using System.IO;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace OneBox.Contracts;

public sealed class IpcProtocolException : Exception
{
    public IpcErrorCode ErrorCode { get; }

    public IpcProtocolException(IpcErrorCode errorCode, string message) : base(message)
    {
        ErrorCode = errorCode;
    }
}

public static class IpcFraming
{
    public static async Task WriteAsync<T>(Stream stream, T message, CancellationToken cancellationToken, TimeSpan? timeout = null)
    {
        ArgumentNullException.ThrowIfNull(stream);
        byte[] body = JsonSerializer.SerializeToUtf8Bytes(message, IpcJson.Options);
        if (body.Length == 0 || body.Length > IpcProtocol.MaxMessageBytes)
            throw new IpcProtocolException(IpcErrorCode.PayloadTooLarge, "Message exceeds the protocol limit.");

        byte[] header = new byte[sizeof(int)];
        BinaryPrimitives.WriteInt32LittleEndian(header, body.Length);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        linked.CancelAfter(timeout ?? IpcProtocol.WriteTimeout);
        try
        {
            await stream.WriteAsync(header, linked.Token).ConfigureAwait(false);
            await stream.WriteAsync(body, linked.Token).ConfigureAwait(false);
            await stream.FlushAsync(linked.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new IpcProtocolException(IpcErrorCode.Timeout, "Timed out writing IPC message.");
        }
    }

    public static async Task<T> ReadAsync<T>(Stream stream, CancellationToken cancellationToken, TimeSpan? timeout = null)
    {
        ArgumentNullException.ThrowIfNull(stream);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        linked.CancelAfter(timeout ?? IpcProtocol.ReadTimeout);
        try
        {
            byte[] header = new byte[sizeof(int)];
            await stream.ReadExactlyAsync(header, linked.Token).ConfigureAwait(false);
            int length = BinaryPrimitives.ReadInt32LittleEndian(header);
            if (length <= 0)
                throw new IpcProtocolException(IpcErrorCode.InvalidMessage, "Message length must be positive.");
            if (length > IpcProtocol.MaxMessageBytes)
                throw new IpcProtocolException(IpcErrorCode.PayloadTooLarge, "Message exceeds the protocol limit.");

            byte[] body = new byte[length];
            await stream.ReadExactlyAsync(body, linked.Token).ConfigureAwait(false);
            try
            {
                T value = JsonSerializer.Deserialize<T>(body, IpcJson.Options);
                return value ?? throw new IpcProtocolException(IpcErrorCode.InvalidMessage, "Message body is empty.");
            }
            catch (JsonException ex)
            {
                throw new IpcProtocolException(IpcErrorCode.InvalidMessage, "Message body is not valid JSON: " + ex.Message);
            }
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new IpcProtocolException(IpcErrorCode.Timeout, "Timed out reading IPC message.");
        }
        catch (EndOfStreamException ex)
        {
            throw new IpcProtocolException(IpcErrorCode.InvalidMessage, "IPC message ended before the declared length: " + ex.Message);
        }
    }
}
