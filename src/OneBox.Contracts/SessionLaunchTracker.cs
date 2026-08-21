using System;
using System.Collections.Generic;
using System.Linq;

namespace OneBox.Contracts;

public sealed class SessionLaunchTracker
{
    private readonly Dictionary<int, State> _states = new();
    private readonly TimeSpan _initialDelay;
    private readonly TimeSpan _maximumDelay;

    public SessionLaunchTracker(TimeSpan? initialDelay = null, TimeSpan? maximumDelay = null)
    {
        _initialDelay = initialDelay ?? TimeSpan.FromSeconds(5);
        _maximumDelay = maximumDelay ?? TimeSpan.FromMinutes(1);
        if (_initialDelay <= TimeSpan.Zero || _maximumDelay < _initialDelay)
            throw new ArgumentOutOfRangeException(nameof(initialDelay));
    }

    public bool ShouldAttempt(int sessionId, DateTimeOffset now)
    {
        if (!_states.TryGetValue(sessionId, out State state)) return true;
        return !state.Completed && now >= state.NextAttempt;
    }

    public void RecordCompleted(int sessionId) =>
        _states[sessionId] = new State(true, 0, DateTimeOffset.MaxValue);

    public TimeSpan RecordFailure(int sessionId, DateTimeOffset now)
    {
        int failures = _states.TryGetValue(sessionId, out State state) ? state.Failures + 1 : 1;
        double multiplier = Math.Pow(2, Math.Min(failures - 1, 20));
        TimeSpan delay = TimeSpan.FromMilliseconds(Math.Min(
            _maximumDelay.TotalMilliseconds,
            _initialDelay.TotalMilliseconds * multiplier));
        _states[sessionId] = new State(false, failures, now + delay);
        return delay;
    }

    public void Synchronize(IEnumerable<int> activeSessionIds)
    {
        var active = activeSessionIds.ToHashSet();
        foreach (int sessionId in _states.Keys.Where(id => !active.Contains(id)).ToArray())
            _states.Remove(sessionId);
    }

    public bool IsTracked(int sessionId) => _states.ContainsKey(sessionId);

    private readonly record struct State(bool Completed, int Failures, DateTimeOffset NextAttempt);
}
