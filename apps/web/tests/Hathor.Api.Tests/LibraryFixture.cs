using System.Net.Http.Json;
using Microsoft.AspNetCore.Mvc.Testing;

namespace Hathor.Api.Tests;

// Isolated fixture for API tests. Single-account mode means one user per
// database, so every test gets a fresh server: unique temp library folders
// AND a unique Postgres database (never the shared desktop folders or dev DB).
// Instantiate per test (xUnit news up the test class per test); Dispose
// cleans up afterwards.
public sealed class TempLibraryFixture : IAsyncLifetime
{
    public WebApplicationFactory<Program> Factory { get; private set; } = null!;
    public HttpClient Client { get; private set; } = null!;
    public string SongsDir { get; private set; } = "";
    public string PodcastsDir { get; private set; } = "";
    private WebApplicationFactory<Program> _base = null!;
    private string _libDir = "";
    private string _database = "";

    public async Task InitializeAsync()
    {
        _base = new WebApplicationFactory<Program>();
        _libDir = Path.Combine(Path.GetTempPath(), "hathor-test-" + Guid.NewGuid().ToString("N"));
        SongsDir = Path.Combine(_libDir, "songs");
        PodcastsDir = Path.Combine(_libDir, "podcasts");
        Directory.CreateDirectory(SongsDir);
        Directory.CreateDirectory(PodcastsDir);
        var (connectionString, database) = await TestPostgres.CreateDatabaseAsync("hathor_apitest");
        _database = database;
        // The fixture connection string already embeds its password, but the
        // merged Database:Password (dev secrets file) would override just
        // that part — pin it to the test password at the test layer.
        var testPassword = new Npgsql.NpgsqlConnectionStringBuilder(
            TestPostgres.AdminConnectionString).Password ?? "hathor";
        // Test-layer config via ConfigureAppConfiguration (runs at host
        // build, i.e. above appsettings files AND the dev secrets file):
        // the whole Database layer is pinned to this fixture so lower
        // layers (dev secrets with a Host part) can never hijack it.
        Factory = _base.WithWebHostBuilder(b => b
            .UseSetting("Library:SongsPath", SongsDir)
            .UseSetting("Library:PodcastsPath", PodcastsDir)
            .UseSetting("Database:Provider", "postgres")
            .UseSetting("Database:ConnectionString", connectionString)
            .UseSetting("Database:Host", "")
            .UseSetting("Database:Password", testPassword)
            // Keep the lab-Postgres sink a no-op in tests (no shared logs
            // DB required); sink behavior is covered by dedicated tests.
            .UseSetting("Logging:ConnectionString", "")
            .ConfigureAppConfiguration((_, cfg) => cfg.Add(new DictSource(
                new Dictionary<string, string?>
                {
                    ["Library:SongsPath"] = SongsDir,
                    ["Library:PodcastsPath"] = PodcastsDir,
                    ["Database:Provider"] = "postgres",
                    ["Database:ConnectionString"] = connectionString,
                    ["Database:Host"] = "",
                    ["Database:Password"] = testPassword,
                    ["Logging:ConnectionString"] = "",
                }))));
        Client = Factory.CreateClient();
    }

    public static System.Net.Http.Headers.AuthenticationHeaderValue Bearer(string token) =>
        new("Bearer", token);

    public async Task<string> RegisterAsync(string username, string password = "phase-test-123")
    {
        var reg = await Client.PostAsJsonAsync("/api/v1/auth/register",
            new { username, password });
        reg.EnsureSuccessStatusCode();
        return (await reg.Content.ReadFromJsonAsync<Tokens>())!.AccessToken;
    }

    public async Task DisposeAsync()
    {
        Factory.Dispose();
        _base.Dispose();
        try { if (Directory.Exists(_libDir)) Directory.Delete(_libDir, recursive: true); }
        catch { }
        if (!string.IsNullOrEmpty(_database))
        {
            try { await TestPostgres.DropDatabaseAsync(_database); }
            catch { }
        }
    }

    private sealed record Tokens(string AccessToken, string RefreshToken, string Username);
}
