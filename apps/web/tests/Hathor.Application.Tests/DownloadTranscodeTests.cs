using FluentAssertions;
using Hathor.Infrastructure.Ingest;

namespace Hathor.Application.Tests;

// ffmpeg stderr parsing for transcode progress (the 0-byte-output hang was
// a filled stderr pipe: draining doubles as the progress source).
public sealed class DownloadTranscodeTests
{
    [Fact]
    public void TryParseDuration_ReadsHeaderLine()
    {
        YoutubeExplodeEngine.TryParseDuration(
                "  Duration: 00:17:42.63, start: 0.000000, bitrate: 192 kb/s")
            .Should().Be(TimeSpan.FromSeconds(17 * 60 + 42.63));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("size= 5120KiB time=00:02:31.04 bitrate= 277.4kbits/s")]
    [InlineData("Duration: N/A")]
    public void TryParseDuration_RejectsNonHeaders(string? line) =>
        YoutubeExplodeEngine.TryParseDuration(line ?? "").Should().BeNull();

    [Fact]
    public void TranscodeFraction_MapsTimeAgainstDuration()
    {
        YoutubeExplodeEngine.TranscodeFraction(
                "size=  5120KiB time=00:02:31.04 bitrate= 277.4kbits/s",
                TimeSpan.FromMinutes(17))
            .Should().BeApproximately(151.04 / 1020, 0.0001);
    }

    [Fact]
    public void TranscodeFraction_ClampsPastEnd()
    {
        YoutubeExplodeEngine.TranscodeFraction("time=01:00:00.00", TimeSpan.FromMinutes(17))
            .Should().Be(1.0);
    }

    [Theory]
    [InlineData("frame= 1234 fps=0.0")]
    [InlineData("")]
    public void TranscodeFraction_RejectsLinesWithoutTime(string line) =>
        YoutubeExplodeEngine.TranscodeFraction(line, TimeSpan.FromMinutes(17)).Should().BeNull();

    [Fact]
    public void TranscodeFraction_RejectsZeroDuration() =>
        YoutubeExplodeEngine.TranscodeFraction("time=00:01:00.00", TimeSpan.Zero).Should().BeNull();
}
