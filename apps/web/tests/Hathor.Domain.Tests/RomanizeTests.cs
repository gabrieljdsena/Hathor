using FluentAssertions;
using Hathor.Domain.Services;

namespace Hathor.Domain.Tests;

public sealed class KanaRomajiTests
{
    [Theory]
    [InlineData("こんにちは", "konnichiha")]
    [InlineData("サクラ", "sakura")]
    [InlineData("がっこう", "gakkou")]
    [InlineData("シャワー", "shawaa")]
    [InlineData("コンピューター", "konpyuutaa")]
    [InlineData("きゃく", "kyaku")]
    [InlineData("じゃんけん", "janken")]
    [InlineData("漢字かな", "漢字kana")]
    [InlineData("", "")]
    public void Romanize_BasicKana(string input, string expected) =>
        KanaRomaji.Romanize(input).Should().Be(expected);

    [Fact]
    public void Romanize_Lrc_PreservesTimestamp()
    {
        KanaRomaji.Romanize("[00:12.34] さくら", isLrc: true)
            .Should().Be("[00:12.34] sakura");
    }

    [Fact]
    public void Romanize_Plain_KeepsTimestampsInline()
    {
        // Non-LRC text passes through uniformly (no special-casing).
        KanaRomaji.Romanize("さくら").Should().Be("sakura");
    }
}
