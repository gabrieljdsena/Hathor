using FluentAssertions;
using Hathor.Application.Enrichment;
using Hathor.Application.Ports;
using NSubstitute;

namespace Hathor.Application.Tests;

public sealed class RomanizeTextTests
{
    private static IRomanizerBackend Backend(string? text)
    {
        var backend = Substitute.For<IRomanizerBackend>();
        backend.RomanizeAsync(Arg.Any<string>(), Arg.Any<bool>(), Arg.Any<CancellationToken>())
            .Returns(text);
        return backend;
    }

    [Fact]
    public async Task BackendText_Wins_WhenAvailable()
    {
        var dto = await new RomanizeTextHandler(Backend("sakura ga mau"))
            .Handle(new RomanizeTextQuery("桜が舞う", false), CancellationToken.None);

        dto.Should().Be("sakura ga mau");
    }

    [Fact]
    public async Task NullBackend_FallsBackToKana()
    {
        var dto = await new RomanizeTextHandler(Backend(null))
            .Handle(new RomanizeTextQuery("さくら", false), CancellationToken.None);

        dto.Should().Be("sakura");
    }

    [Fact]
    public async Task BlankBackend_FallsBackToKana()
    {
        var dto = await new RomanizeTextHandler(Backend("   "))
            .Handle(new RomanizeTextQuery("さくら", false), CancellationToken.None);

        dto.Should().Be("sakura");
    }

    [Fact]
    public async Task LrcTimestamps_Preserved_ByFallback()
    {
        var dto = await new RomanizeTextHandler(Backend(null))
            .Handle(new RomanizeTextQuery("[00:12.34] さくら", true), CancellationToken.None);

        dto.Should().Be("[00:12.34] sakura");
    }

    [Fact]
    public async Task EmptyInput_ReturnsEmpty_WithoutCallingBackend()
    {
        var backend = Backend("should-not-be-used");

        var dto = await new RomanizeTextHandler(backend)
            .Handle(new RomanizeTextQuery("", false), CancellationToken.None);

        dto.Should().Be("");
        await backend.DidNotReceive().RomanizeAsync(
            Arg.Any<string>(), Arg.Any<bool>(), Arg.Any<CancellationToken>());
    }
}
