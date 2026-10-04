using Microsoft.Extensions.Configuration;

namespace Hathor.Api.Tests;

// Layered test configuration (highest precedence wins, like real hosts).
internal sealed class DictSource(Dictionary<string, string?> values) : IConfigurationSource
{
    public IConfigurationProvider Build(IConfigurationBuilder builder) => new DictProvider(values);

    private sealed class DictProvider(Dictionary<string, string?> values) : ConfigurationProvider
    {
        public override void Load() =>
            Data = new Dictionary<string, string?>(values, StringComparer.OrdinalIgnoreCase);
    }
}
