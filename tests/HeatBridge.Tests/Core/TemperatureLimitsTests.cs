using HeatBridge.Core.Devices;

namespace HeatBridge.Tests.Core;

public class TemperatureLimitsTests
{
    private readonly TemperatureLimits _limits = new(17, 30, 0.5);

    [Theory]
    [InlineData(17.0, true)]
    [InlineData(24.5, true)]
    [InlineData(30.0, true)]
    [InlineData(16.5, false)]
    [InlineData(30.5, false)]
    [InlineData(24.3, false)]
    [InlineData(double.NaN, false)]
    public void IsValid_EnforcesRangeAndStep(double value, bool expected)
    {
        Assert.Equal(expected, _limits.IsValid(value));
    }

    [Theory]
    [InlineData(10, 17)]
    [InlineData(35, 30)]
    [InlineData(24.3, 24.5)]
    [InlineData(24.2, 24.0)]
    [InlineData(24.25, 24.5)]
    public void Normalize_ClampsAndSnapsToStep(double value, double expected)
    {
        Assert.Equal(expected, _limits.Normalize(value));
    }

    [Theory]
    [InlineData(24.5, 1, 25.0)]
    [InlineData(24.5, -1, 24.0)]
    [InlineData(24.5, 3, 26.0)]
    [InlineData(30.0, 1, 30.0)]
    [InlineData(17.0, -1, 17.0)]
    [InlineData(29.5, 4, 30.0)]
    public void Offset_MovesInStepsWithinRange(double value, int steps, double expected)
    {
        Assert.Equal(expected, _limits.Offset(value, steps));
    }

    [Fact]
    public void CanIncreaseAndDecrease_StopAtTheLimits()
    {
        Assert.False(_limits.CanIncrease(30));
        Assert.True(_limits.CanIncrease(29.5));
        Assert.False(_limits.CanDecrease(17));
        Assert.True(_limits.CanDecrease(17.5));
    }

    [Fact]
    public void Offset_AccumulatesWithoutFloatingPointDrift()
    {
        var value = 17.0;
        for (var i = 0; i < 26; i++)
        {
            value = _limits.Offset(value, 1);
        }

        Assert.Equal(30.0, value);
        Assert.True(_limits.IsValid(value));
    }

    [Fact]
    public void Normalize_NeverExceedsMaximumWhenRangeIsNotAMultipleOfStep()
    {
        var limits = new TemperatureLimits(16, 30.2, 0.5);

        Assert.Equal(30.0, limits.Normalize(30.2));
    }

    [Theory]
    [InlineData(30, 17, 0.5)]
    [InlineData(17, 30, 0)]
    [InlineData(17, 30, -1)]
    [InlineData(double.NaN, 30, 0.5)]
    public void Constructor_RejectsInvalidLimits(double min, double max, double step)
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new TemperatureLimits(min, max, step));
    }
}
