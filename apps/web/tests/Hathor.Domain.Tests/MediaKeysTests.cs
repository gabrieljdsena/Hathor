using FluentAssertions;
using Hathor.Domain.Services;

namespace Hathor.Domain.Tests;

public sealed class MediaKeysTests
{
    [Theory]
    [InlineData("Midnight City", "M83", "Midnight City", "M83")]
    [InlineData("Smells Like Teen Spirit (Remastered 2011)", "NIRVANA", "Smells Like Teen Spirit", "Nirvana")]
    [InlineData("Midnight City - Single", "M83", "Midnight City", "M83")]
    [InlineData("Song feat. Guest", "A", "Song", "A")]
    [InlineData("Don't Stop", "A", "Dont Stop", "A")]
    public void Key_ConvergesVariants(string t1, string a1, string t2, string a2) =>
        MediaKeys.Key(t1, a1).Should().Be(MediaKeys.Key(t2, a2));

    [Theory]
    [InlineData("Time", "Times")]
    [InlineData("Song", "Song 2")]
    public void Key_KeepsDistinctTitlesApart(string t1, string t2) =>
        MediaKeys.Key(t1, "A").Should().NotBe(MediaKeys.Key(t2, "A"));

    [Theory]
    [InlineData("Pink Floyd", "pink floyd", true)]
    [InlineData("M83", "M83 ", true)]
    [InlineData("Nirvana", "Pearl Jam", false)]
    public void IsSameArtist_IgnoresCaseAndSpace(string a, string b, bool expected) =>
        MediaKeys.IsSameArtist(a, b).Should().Be(expected);
}
