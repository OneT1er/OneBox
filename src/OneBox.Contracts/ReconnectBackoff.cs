using System;

namespace OneBox.Contracts;

public sealed class ReconnectBackoff
{
    private readonly TimeSpan _minimum;
    private readonly TimeSpan _maximum;
    private int _attempt;

    public ReconnectBackoff(TimeSpan? minimum = null, TimeSpan? maximum = null)
    {
        _minimum = minimum ?? TimeSpan.FromMilliseconds(500);
        _maximum = maximum ?? TimeSpan.FromSeconds(30);
        if (_minimum <= TimeSpan.Zero || _maximum < _minimum)
            throw new ArgumentOutOfRangeException(nameof(minimum));
    }

    public TimeSpan NextDelay()
    {
        double factor = Math.Pow(2, Math.Min(_attempt++, 20));
        return TimeSpan.FromMilliseconds(Math.Min(_maximum.TotalMilliseconds, _minimum.TotalMilliseconds * factor));
    }

    public void Reset() => _attempt = 0;
}

public sealed class FixedWindowRateLimiter
{
    private readonly int _limit;
    private readonly TimeSpan _window;
    private DateTimeOffset _windowStart;
    private int _count;

    public FixedWindowRateLimiter(int limit, TimeSpan window)
    {
        if (limit <= 0) throw new ArgumentOutOfRangeException(nameof(limit));
        if (window <= TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(window));
        _limit = limit;
        _window = window;
    }

    public bool TryAcquire(DateTimeOffset now)
    {
        lock (this)
        {
            if (_windowStart == default || now - _windowStart >= _window || now < _windowStart)
            {
                _windowStart = now;
                _count = 0;
            }
            if (_count >= _limit) return false;
            _count++;
            return true;
        }
    }
}
