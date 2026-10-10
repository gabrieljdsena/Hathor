using FluentAssertions;
using Hathor.Infrastructure.Dapper;
using Hathor.Infrastructure.Ef;

namespace Hathor.Api.Tests;

public sealed class DatabaseConnectionTests : IAsyncLifetime
{
    private readonly List<string> _databases = [];

    public Task InitializeAsync() => Task.CompletedTask;

    public async Task DisposeAsync()
    {
        foreach (var db in _databases)
        {
            try { await TestPostgres.DropDatabaseAsync(db); }
            catch { }
        }
    }

    [Fact]
    public void Resolve_PasswordOverride_ReplacesOnlyPassword()
    {
        var config = new StubConfig(new Dictionary<string, string?>
        {
            ["Database:Provider"] = "postgres",
            ["Database:ConnectionString"] = "Host=dbhost;Port=5432;Database=hathor;Username=postgres;Password=old",
            ["Database:Password"] = "new-secret",
        });
        var resolved = DatabaseConnection.Resolve(config);
        resolved.Should().Contain("Password=new-secret");
        resolved.Should().Contain("Host=dbhost");
        resolved.Should().Contain("Database=hathor");
        resolved.Should().NotContain("Password=old");
    }

    [Fact]
    public void Resolve_EmptyPassword_FallsBackToEnvironment()
    {
        // Regression: appsettings.json ships "Password": "" — empty must
        // count as unset so HATHOR_DB_PASSWORD still applies (?? alone only
        // falls through on null, which silently dropped the env password
        // and broke service startup after the baked-in default was removed).
        var previous = Environment.GetEnvironmentVariable(DatabaseConnection.PasswordEnvVar);
        try
        {
            Environment.SetEnvironmentVariable(DatabaseConnection.PasswordEnvVar, "env-secret");
            var config = new StubConfig(new Dictionary<string, string?>
            {
                ["Database:ConnectionString"] = "Host=dbhost;Database=hathor;Username=postgres",
                ["Database:Password"] = "",
            });
            DatabaseConnection.Resolve(config).Should().Contain("Password=env-secret");
        }
        finally
        {
            Environment.SetEnvironmentVariable(DatabaseConnection.PasswordEnvVar, previous);
        }
    }

    [Fact]
    public void Resolve_PlaceholderPassword_KeepsConnectionString()
    {
        var config = new StubConfig(new Dictionary<string, string?>
        {
            ["Database:ConnectionString"] = "Host=dbhost;Database=hathor;Username=postgres;Password=old",
            ["Database:Password"] = "REPLACE_WITH_LOCAL_POSTGRES_PASSWORD",
        });
        DatabaseConnection.Resolve(config).Should().Contain("Password=old");
    }

    [Fact]
    public void ApplyPassword_MySql_ReplacesPassword()
    {
        var resolved = DatabaseConnection.ApplyPassword(
            "mysql", "server=db;database=hathor;user=root;password=old", "new-secret");
        resolved.Should().Contain("Password=new-secret");
        resolved.Should().Contain("Server=db");
    }

    [Fact]
    public void Resolve_HostPart_BuildsFromParts_WhenNoConnectionString()
    {
        var config = new StubConfig(new Dictionary<string, string?>
        {
            ["Database:Provider"] = "postgres",
            ["Database:ConnectionString"] = "",
            ["Database:Host"] = "dbhost",
            ["Database:Database"] = "hathor",
            ["Database:Password"] = "s3cret",
        });
        var resolved = DatabaseConnection.Resolve(config);
        resolved.Should().Contain("Host=dbhost");
        resolved.Should().Contain("Password=s3cret");
    }

    [Fact]
    public void Resolve_HighestPrecedenceLayer_WinsAsAWhole()
    {
        // Secrets-like layer (Host parts) below an override layer (full
        // connection string): the override wins, the Host must not hijack.
        var config = new Microsoft.Extensions.Configuration.ConfigurationBuilder()
            .Add(new DictSource(new Dictionary<string, string?>
            {
                ["Database:Host"] = "nativehost",
                ["Database:Password"] = "native-secret",
            }))
            .Add(new DictSource(new Dictionary<string, string?>
            {
                ["Database:ConnectionString"] = "Host=override;Database=hathor;Username=postgres",
            }))
            .Build();
        var resolved = DatabaseConnection.Resolve(config);
        resolved.Should().Contain("Host=override");
        resolved.Should().NotContain("nativehost");
    }

    [Fact]
    public void Resolve_PartsWin_WhenHigherLayerHasOnlyParts()
    {
        // Reversed: override layer carries only Host parts while a lower
        // layer has the full default — parts win.
        var config = new Microsoft.Extensions.Configuration.ConfigurationBuilder()
            .Add(new DictSource(new Dictionary<string, string?>
            {
                ["Database:ConnectionString"] = "Host=defaulted;Database=hathor;Username=postgres",
            }))
            .Add(new DictSource(new Dictionary<string, string?>
            {
                ["Database:Host"] = "dbhost",
                ["Database:Database"] = "hathor",
            }))
            .Build();
        var resolved = DatabaseConnection.Resolve(config);
        resolved.Should().Contain("Host=dbhost");
        resolved.Should().NotContain("defaulted");
    }
}