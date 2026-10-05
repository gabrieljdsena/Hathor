using FluentAssertions;
using Hathor.Domain.Services;

namespace Hathor.Domain.Tests;

public sealed class LoudnessGainTests
{
    [Fact]
    public void Constants_MatchFrontendEngine()
    {
        LoudnessGain.TargetLufs.Should().Be(-14.0);
        LoudnessGain.MaxCorrectionDb.Should().Be(12.0);
    }

    [Theory]
    [InlineData(null, 0.0)]
    [InlineData(-14.0, 0.0)] // at target: no correction
    [InlineData(-6.0, -8.0)] // hot track turned down
    [InlineData(-23.0, 9.0)] // quiet track lifted
    [InlineData(5.0, -12.0)] // absurd reading clamps
    [InlineData(-40.0, 12.0)] // absurd reading clamps
    public void CorrectionDb_TargetsLufs_Clamped(double? measured, double expected) =>
        LoudnessGain.CorrectionDb(measured).Should().BeApproximately(expected, 1e-9);

    [Theory]
    [InlineData(0.0, 1.0)]
    [InlineData(6.0, 1.9952623149688795)]
    [InlineData(-20.0, 0.251188643150958)] // clamps to -12 dB
    public void ToLinear_ConvertsDb_Clamped(double gainDb, double expected) =>
        LoudnessGain.ToLinear(gainDb).Should().BeApproximately(expected, 1e-9);
}
