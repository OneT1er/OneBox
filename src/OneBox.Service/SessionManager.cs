using System.Collections.Concurrent;
using OneBox.Contracts;

namespace OneBox.Service;

internal sealed class SessionManager
{
    private readonly ConcurrentDictionary<string, UserRuntime> _runtimes = new(StringComparer.OrdinalIgnoreCase);
    private readonly SessionLaunchTracker _launches = new();

    public async Task RefreshAsync(CancellationToken cancellationToken)
    {
        IReadOnlyList<InteractiveSession> active = SessionInterop.EnumerateActiveSessions();
        var activeSids = active.Select(item => item.UserSid).ToHashSet(StringComparer.OrdinalIgnoreCase);
        _launches.Synchronize(active.Select(item => item.SessionId));

        foreach (InteractiveSession session in active)
        {
            _runtimes.GetOrAdd(session.UserSid, sid =>
            {
                ServiceLog.Write($"starting user runtime sid={sid}");
                var runtime = new UserRuntime(sid);
                runtime.Start();
                return runtime;
            });

            DateTimeOffset now = DateTimeOffset.UtcNow;
            if (!_launches.ShouldAttempt(session.SessionId, now)) continue;
            if (!SessionInterop.IsServiceAutoStartEnabled(session.SessionId))
            {
                _launches.RecordCompleted(session.SessionId);
                continue;
            }

            if (SessionInterop.LaunchGui(session.SessionId, Path.Combine(AppContext.BaseDirectory, ServiceConstants.GuiExecutable)))
            {
                _launches.RecordCompleted(session.SessionId);
            }
            else
            {
                TimeSpan delay = _launches.RecordFailure(session.SessionId, now);
                ServiceLog.Write($"GUI launch will retry session={session.SessionId} in {delay.TotalSeconds:0}s");
            }
        }

        foreach ((string sid, UserRuntime runtime) in _runtimes.ToArray())
        {
            if (activeSids.Contains(sid)) continue;
            if (_runtimes.TryRemove(sid, out _)) await runtime.StopAsync().ConfigureAwait(false);
        }
    }

    public async Task StopAsync()
    {
        foreach ((string sid, UserRuntime runtime) in _runtimes.ToArray())
        {
            if (_runtimes.TryRemove(sid, out _)) await runtime.StopAsync().ConfigureAwait(false);
        }
    }
}
