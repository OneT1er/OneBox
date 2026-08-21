using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace PowerAudioManager.Commands
{
    public sealed class AppCommandDispatcher : IAppCommandDispatcher
    {
        readonly Func<CommandRequest, Task<CommandResult>> _handler;
        readonly Action<string, Exception> _logError;
        readonly ConcurrentDictionary<AppCommandId, byte> _running = new();
        int _exitStarted;

        public AppCommandDispatcher(Func<CommandRequest, Task<CommandResult>> handler,
            Action<string, Exception> logError = null)
        {
            _handler = handler ?? throw new ArgumentNullException(nameof(handler));
            _logError = logError;
            RegisteredCommandIds = Array.AsReadOnly(AppCommandCatalog.All.Select(x => x.Id).ToArray());
        }

        public IReadOnlyCollection<AppCommandId> RegisteredCommandIds { get; }

        public async Task<CommandResult> DispatchAsync(CommandRequest request)
        {
            if (request == null)
                return CommandResult.Fail(CommandErrorCode.InvalidPayload, "命令请求不能为空。");
            if (!AppCommandCatalog.TryGet(request.CommandId, out var definition))
                return CommandResult.Fail(CommandErrorCode.UnknownCommand, "不支持的功能指令。");
            if (definition.PayloadType == null ? request.Payload != null
                : request.Payload == null || !definition.PayloadType.IsInstanceOfType(request.Payload))
                return CommandResult.Fail(CommandErrorCode.InvalidPayload,
                    $"{definition.Term}的参数无效。");
            if (request.CancellationToken.IsCancellationRequested) return CommandResult.Cancelled();

            if (request.CommandId == AppCommandId.AppExit &&
                Interlocked.CompareExchange(ref _exitStarted, 1, 0) != 0)
                return CommandResult.Fail(CommandErrorCode.Busy, "退出操作已经开始。");

            bool entered = !definition.PreventReentry || _running.TryAdd(request.CommandId, 0);
            if (!entered) return CommandResult.Fail(CommandErrorCode.Busy, definition.Term + "正在执行，请稍候。");

            try
            {
                return await _handler(request).ConfigureAwait(false)
                    ?? CommandResult.Fail(CommandErrorCode.Failed, definition.Term + "未返回执行结果。");
            }
            catch (OperationCanceledException) when (request.CancellationToken.IsCancellationRequested)
            {
                return CommandResult.Cancelled();
            }
            catch (AppCommandPayloadException ex)
            {
                _logError?.Invoke(definition.Term, ex);
                return CommandResult.Fail(CommandErrorCode.InvalidPayload, ex.Message);
            }
            catch (Exception ex)
            {
                _logError?.Invoke(definition.Term, ex);
                return CommandResult.Fail(CommandErrorCode.Failed, definition.Term + "失败：" + ex.Message);
            }
            finally
            {
                if (definition.PreventReentry) _running.TryRemove(request.CommandId, out _);
            }
        }
    }
}
