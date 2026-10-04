using System.Net;
using FluentAssertions;
using Hathor.Infrastructure.Enrichment;
using Microsoft.Extensions.Logging;
using NSubstitute;

namespace Hathor.Application.Tests;

public sealed class DiscoverySuggesterTests
{
    private const string ChatJson =
        """{"choices": [{"message": {"content": "{\"suggestions\": [{\"title\": \"Midnight City\", \"artist\": \"M83\"}, {\"title\": \"\", \"artist\": \"Nope\"}, {\"title\": \"Outro\", \"artist\": \"M83\"}]}"}}]}""";

    private static LlamaDiscoverySuggester Suggester(
        HttpMessageHandler handler, DiscoveryLlmOptions? options = null) =>
        new(
            FakeFactory(handler),
            options ?? new DiscoveryLlmOptions { Model = "test-model" },
            Substitute.For<ILogger<LlamaDiscoverySuggester>>());

    private static IHttpClientFactory FakeFactory(HttpMessageHandler handler)
    {
        var factory = Substitute.For<IHttpClientFactory>();
        factory.CreateClient("llm").Returns(new HttpClient(handler));
        return factory;
    }

    private sealed class FakeHandler(string json) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(json),
            });
    }

    private sealed class CapturingHandler(string json) : HttpMessageHandler
    {
        public HttpRequestMessage? Seen;
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Seen = request;
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(json),
            });
        }
    }

    private sealed class FailingHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromException<HttpResponseMessage>(new HttpRequestException("nope"));
    }

    [Fact]
    public async Task SuggestAsync_ParsesJsonSuggestions_SkippingBlanks()
    {
        var result = await Suggester(new FakeHandler(ChatJson))
            .SuggestAsync([("M83", 7)], 10, CancellationToken.None);

        result.Should().HaveCount(2);
        result[0].Should().Be(("Midnight City", "M83"));
        result[1].Should().Be(("Outro", "M83"));
    }

    [Fact]
    public async Task SuggestAsync_CapsAtMaxSuggestions()
    {
        var result = await Suggester(new FakeHandler(ChatJson))
            .SuggestAsync([("M83", 7)], 1, CancellationToken.None);

        result.Should().HaveCount(1);
    }

    [Fact]
    public async Task SuggestAsync_PostsToChatCompletions()
    {
        var handler = new CapturingHandler(ChatJson);
        await Suggester(handler).SuggestAsync([("M83", 7)], 10, CancellationToken.None);

        handler.Seen.Should().NotBeNull();
        handler.Seen!.RequestUri!.ToString().Should().Be(
            "http://localhost:1234/v1/chat/completions");
        handler.Seen.Method.Should().Be(HttpMethod.Post);
    }

    [Fact]
    public async Task SuggestAsync_GarbageReply_YieldsEmpty()
    {
        var result = await Suggester(new FakeHandler("not json at all {{{"))
            .SuggestAsync([("M83", 7)], 10, CancellationToken.None);

        result.Should().BeEmpty();
    }

    [Fact]
    public async Task SuggestAsync_LlmDown_YieldsEmpty()
    {
        var result = await Suggester(new FailingHandler())
            .SuggestAsync([("M83", 7)], 10, CancellationToken.None);

        result.Should().BeEmpty();
    }

    [Fact]
    public async Task SuggestAsync_Disabled_MakesNoHttpCall()
    {
        var factory = Substitute.For<IHttpClientFactory>();
        var suggester = new LlamaDiscoverySuggester(
            factory,
            new DiscoveryLlmOptions { LlmEnabled = false, Model = "test-model" },
            Substitute.For<ILogger<LlamaDiscoverySuggester>>());

        var result = await suggester.SuggestAsync([("M83", 7)], 10, CancellationToken.None);

        result.Should().BeEmpty();
        factory.DidNotReceive().CreateClient(Arg.Any<string>());
    }

    [Fact]
    public async Task SuggestAsync_EmptyModel_MakesNoHttpCall()
    {
        var factory = Substitute.For<IHttpClientFactory>();
        var suggester = new LlamaDiscoverySuggester(
            factory,
            new DiscoveryLlmOptions { Model = "" },
            Substitute.For<ILogger<LlamaDiscoverySuggester>>());

        var result = await suggester.SuggestAsync([("M83", 7)], 10, CancellationToken.None);

        result.Should().BeEmpty();
        factory.DidNotReceive().CreateClient(Arg.Any<string>());
    }

    [Fact]
    public async Task SuggestAsync_EmptyTaste_MakesNoHttpCall()
    {
        var factory = Substitute.For<IHttpClientFactory>();
        var suggester = new LlamaDiscoverySuggester(
            factory,
            new DiscoveryLlmOptions { Model = "test-model" },
            Substitute.For<ILogger<LlamaDiscoverySuggester>>());

        var result = await suggester.SuggestAsync([], 10, CancellationToken.None);

        result.Should().BeEmpty();
        factory.DidNotReceive().CreateClient(Arg.Any<string>());
    }
}
