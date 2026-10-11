using System.Data;
using Microsoft.Extensions.Configuration;
using Npgsql;

namespace Hathor.Infrastructure.Dapper;

// Database connection resolution shared by EF Core and Dapper (plan §2):
// one connection string drives both. Postgres-only: the MySQL/TiDB remote
// is retired, so the provider switch is gone. The password never has to
// live in the connection string itself — "Database:Password" (gitignored
// secrets file) or HATHOR_DB_PASSWORD overrides just that part, so local
// `dotnet run` against a native Postgres only needs the password.
public static class DatabaseConnection
{
    public const string PasswordEnvVar = "HATHOR_DB_PASSWORD";

    public static string Provider(IConfiguration config) =>
        config.GetValue("Database:Provider", "postgres")?.ToLowerInvariant() ?? "postgres";

    public static string Resolve(IConfiguration config)
    {
        // Layer-aware: the highest-precedence config source that mentions
        // the database wins as a whole (its connection string, else its
        // parts). A Host part in one layer must not hijack a full
        // connection string from a higher layer (e.g. test overrides).
        var provider = Provider(config);
        if (provider != "postgres")
            throw new InvalidOperationException(
                $"Unsupported Database:Provider '{provider}'. Hathor is Postgres-only.");
        var connectionString = HighestPrecedenceConnection(config)
            ?? DefaultConnectionString();
        // Whitespace (e.g. "Password": "" shipped in appsettings.json)
        // counts as unset so the environment fallback still applies — ??
        // alone only falls through on null.
        var password = config.GetValue<string>("Database:Password");
        if (string.IsNullOrWhiteSpace(password))
            password = Environment.GetEnvironmentVariable(PasswordEnvVar);
        if (IsPlaceholder(password)) return connectionString;
        return ApplyPassword(connectionString, password!);
    }

    private static string? HighestPrecedenceConnection(IConfiguration config)
    {
        if (config is IConfigurationRoot root)
        {
            foreach (var source in root.Providers.Reverse())
            {
                if (source.TryGet("Database:ConnectionString", out var cs) && !IsPlaceholder(cs))
                    return cs;
                if (source.TryGet("Database:Host", out var host) && !IsPlaceholder(host))
                    return BuildFromParts(config);
            }
            return null;
        }
        // Non-root configs (test stubs): merged view.
        var mergedCs = config.GetValue<string>("Database:ConnectionString");
        if (!IsPlaceholder(mergedCs)) return mergedCs;
        var mergedHost = config.GetValue<string>("Database:Host");
        return IsPlaceholder(mergedHost) ? null : BuildFromParts(config);
    }

    // NOTE: no password baked in — Database:Password (gitignored secrets
    // file) or HATHOR_DB_PASSWORD is applied over this base string by
    // Resolve(). Keeps scanners quiet and dev defaults out of git.
    private static string DefaultConnectionString() =>
        "Host=localhost;Port=5432;Database=hathor;Username=postgres";

    private static string? BuildFromParts(IConfiguration config)
    {
        var host = config.GetValue<string>("Database:Host");
        if (IsPlaceholder(host)) return null;
        var user = config.GetValue<string>("Database:Username");
        var database = config.GetValue<string>("Database:Database");
        return new NpgsqlConnectionStringBuilder
        {
            Host = host,
            Port = config.GetValue<int?>("Database:Port") ?? 5432,
            Database = IsPlaceholder(database) ? "hathor" : database,
            Username = IsPlaceholder(user) ? "postgres" : user,
        }.ConnectionString;
    }

    internal static string ApplyPassword(string connectionString, string password) =>
        new NpgsqlConnectionStringBuilder(connectionString) { Password = password }.ConnectionString;

    // An unfilled secrets example must behave like "not configured".
    private static bool IsPlaceholder(string? value) =>
        string.IsNullOrWhiteSpace(value) || value.Contains("REPLACE_WITH_");
}

// One connection string drives both EF Core and Dapper (plan §2).
// Postgres-only: the provider switch is gone (Resolve rejects mysql).
public sealed class DapperConnectionFactory(IConfiguration config)
{
    public string Provider { get; } = DatabaseConnection.Provider(config);

    public string ConnectionString { get; } = DatabaseConnection.Resolve(config);

    public IDbConnection Create() => new NpgsqlConnection(ConnectionString);

    // Postgres folds unquoted identifiers to lowercase while EF creates
    // quoted PascalCase tables/columns, so Postgres SQL must quote.
    public string Quote(string identifier) => $"\"{identifier}\"";

    // EF stores Guids as native uuid on Postgres, where UPPER(uuid) is
    // invalid — cast to text first. The @UserId parameter stays the
    // uppercase "D" form (see UserKey).
    public string UserIdPredicate(string? tableAlias = null)
    {
        var column = tableAlias is null
            ? Quote("UserId")
            : $"{Quote(tableAlias)}.{Quote("UserId")}";
        return $"UPPER({column}::text) = @UserId";
    }

    public static string UserKey(Guid userId) => userId.ToString("D").ToUpperInvariant();
}

public sealed class DatabaseOptions
{
    public string Provider { get; set; } = "postgres";
    public string ConnectionString { get; set; } =
        "Host=localhost;Port=5432;Database=hathor;Username=postgres";
    public string StorageRoot { get; set; } = "data";
}
