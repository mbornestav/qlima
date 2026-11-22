namespace HeatBridge.Agent;

/// <summary>
/// Exponential backoff with ±20 % jitter: min, 2×min, 4×min … capped at max. Jitter avoids many agents
/// reconnecting in lock-step after a server restart.
/// </summary>
public sealed class ReconnectBackoff
{
    private readonly TimeSpan _minimum;
    private readonly TimeSpan _maximum;
    private readonly Func<double> _random;

    public ReconnectBackoff(TimeSpan minimum, TimeSpan maximum, Func<double>? random = null)
    {
        if (minimum <= TimeSpan.Zero || maximum < minimum)
        {
            throw new ArgumentOutOfRangeException(nameof(minimum), "Minimum must be positive and not larger than maximum.");
        }

        _minimum = minimum;
        _maximum = maximum;
        _random = random ?? Random.Shared.NextDouble;
    }

    /// <summary>Number of consecutive failed attempts since the last <see cref="Reset"/>.</summary>
    public int Attempt { get; private set; }

    public TimeSpan Next()
    {
        var exponent = Math.Min(Attempt, 30);
        var baseDelay = Math.Min(_minimum.TotalMilliseconds * Math.Pow(2, exponent), _maximum.TotalMilliseconds);
        var jitter = 0.8 + (0.4 * _random());
        Attempt++;

        var delay = Math.Clamp(baseDelay * jitter, _minimum.TotalMilliseconds, _maximum.TotalMilliseconds);
        return TimeSpan.FromMilliseconds(delay);
    }

    public void Reset() => Attempt = 0;
}
