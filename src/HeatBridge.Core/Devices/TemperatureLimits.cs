using System.Globalization;

namespace HeatBridge.Core.Devices;

/// <summary>
/// The target temperature range and resolution reported by a device.
/// All target temperature changes are validated and snapped against these limits.
/// </summary>
public sealed record TemperatureLimits
{
    private const double Tolerance = 0.01;

    public TemperatureLimits(double minimum, double maximum, double step)
    {
        if (!double.IsFinite(minimum) || !double.IsFinite(maximum) || minimum > maximum)
        {
            throw new ArgumentOutOfRangeException(nameof(minimum),
                string.Create(CultureInfo.InvariantCulture, $"Invalid temperature range {minimum}..{maximum}."));
        }

        if (!double.IsFinite(step) || step <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(step), "Temperature step must be a positive number.");
        }

        Minimum = minimum;
        Maximum = maximum;
        Step = step;
    }

    public double Minimum { get; }

    public double Maximum { get; }

    public double Step { get; }

    /// <summary>True if <paramref name="value"/> is inside the range and aligned to the step.</summary>
    public bool IsValid(double value) =>
        double.IsFinite(value)
        && value >= Minimum - Tolerance
        && value <= Maximum + Tolerance
        && Math.Abs(Snap(value) - value) < Tolerance;

    /// <summary>Clamps <paramref name="value"/> into the range and rounds it to the nearest step.</summary>
    public double Normalize(double value)
    {
        if (!double.IsFinite(value))
        {
            throw new ArgumentOutOfRangeException(nameof(value), "Temperature must be a finite number.");
        }

        return Snap(Math.Clamp(value, Minimum, Maximum));
    }

    /// <summary>Moves <paramref name="value"/> by a number of steps without leaving the range.</summary>
    public double Offset(double value, int steps) => Normalize(Normalize(value) + (steps * Step));

    public bool CanIncrease(double value) => Normalize(value) + Tolerance < Maximum;

    public bool CanDecrease(double value) => Normalize(value) - Tolerance > Minimum;

    /// <summary>Two temperatures are equal if they differ by less than a hundredth of a degree.</summary>
    public static bool AreEqual(double? a, double? b) =>
        a.HasValue && b.HasValue && Math.Abs(a.Value - b.Value) < Tolerance;

    private double Snap(double value)
    {
        var stepsFromMinimum = Math.Round((value - Minimum) / Step, MidpointRounding.AwayFromZero);
        var snapped = Minimum + (stepsFromMinimum * Step);

        // Snapping can push the value past the maximum when the range is not a multiple of the step.
        if (snapped > Maximum + Tolerance)
        {
            snapped -= Step;
        }

        // Remove floating point noise such as 24.499999999.
        return Math.Round(snapped, 3);
    }
}
