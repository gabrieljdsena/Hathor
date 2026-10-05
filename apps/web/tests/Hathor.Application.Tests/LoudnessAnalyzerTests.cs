using FluentAssertions;
using Hathor.Infrastructure.Enrichment;

namespace Hathor.Application.Tests;

public sealed class LoudnessAnalyzerTests
{
    private const string LoudnormStderr = """
        [Parsed_loudnorm_0 @ 0x1234]
        {
            "input_i" : "-11.23",
            "input_tp" : "-1.50",
            "input_lra" : "7.80",
            "input_thresh" : "-34.20",
            "output_i" : "-16.00",
            "output_tp" : "-1.50",
            "output_lra" : "7.00",
            "output_thresh" : "-34.20",
            "normalization_type" : "dynamic",
            "target_offset" : "0.00"
        }
        """;

    [Fact]
    public void ParseInputLufs_ReadsIntegratedLoudness() =>
        LoudnessAnalyzer.ParseInputLufs(LoudnormStderr).Should().BeApproximately(-11.23, 1e-9);

    [Theory]
    [InlineData("")]
    [InlineData("no json here")]
    [InlineData("{\"output_i\": \"-16.00\"}")]
    [InlineData("{\"input_i\": \"-inf\"}")]
    public void ParseInputLufs_RejectsGarbage(string stderr) =>
        LoudnessAnalyzer.ParseInputLufs(stderr).Should().BeNull();
}
