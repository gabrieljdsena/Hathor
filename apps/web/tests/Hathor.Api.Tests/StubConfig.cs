using Microsoft.Extensions.Configuration;

namespace Hathor.Api.Tests;

// Minimal IConfiguration stub (avoids a config-provider package in tests).
internal sealed class StubConfig(Dictionary<string, string?> values) : IConfiguration
{
    public string? this[string key]
    {
        get => values.TryGetValue(key, out var v) ? v : null;
        set => values[key] = value;
    }

    public IEnumerable<IConfigurationSection> GetChildren() => [];
    public Microsoft.Extensions.Primitives.IChangeToken GetReloadToken() =>
        new Microsoft.Extensions.Primitives.CancellationChangeToken(System.Threading.CancellationToken.None);
    public IConfigurationSection GetSection(string key) => new StubSection(this, key);

    private sealed class StubSection(StubConfig root, string key) : IConfigurationSection
    {
        public string? this[string k]
        {
            get => root[$"{key}:{k}"];
            set => root[$"{key}:{k}"] = value;
        }

        public string Key => key;
        public string Path => key;
        public string? Value { get => root[key]; set => root[key] = value; }
        public IEnumerable<IConfigurationSection> GetChildren() => [];
        public Microsoft.Extensions.Primitives.IChangeToken GetReloadToken() =>
            new Microsoft.Extensions.Primitives.CancellationChangeToken(System.Threading.CancellationToken.None);
        public IConfigurationSection GetSection(string k) => new StubSection(root, $"{key}:{k}");
    }
}
