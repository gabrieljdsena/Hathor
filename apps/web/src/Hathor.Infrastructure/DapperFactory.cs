using System.Data;
using Microsoft.Extensions.Configuration;
using MySqlConnector;
using Npgsql;

namespace Hathor.Infrastructure.Dapper;

// Database connection resolution shared by EF Core and Dapper (plan §2):
// one connection string drives both. The password never has to live in
// the connection string itself — "Database:Password" (gitignored secrets
// file) or HATHOR_DB_PASSWORD overrides just that part, so local `dotnet
// run` against a native Postgres only needs the password, not a full URL.
public static class DatabaseConnection
{
    public const string PasswordEnvVar = "HATHOR_DB_PASSWORD";

    public static string Provider(IConfiguration config) =>
        config.GetValue("Database:Provider", "postgres")?.ToLowerInvariant() ?? "postgres";

    public static string Resolve(IConfiguration config)
    {
        var provider = Provider(config);
        // Layer-aware: the highest-precedence config source that mentions
        // the database wins as a whole (its connection string, else its
        // parts). A Host part in one layer must not hijack a full
        // connection string from a higher layer (e.g. test overrides).
        var connectionString = HighestPrecedenceConnection(config, provider)
            ?? DefaultConnectionString(provider);
        var password = config.GetValue<string>("Database:Password")
            ?? Environment.GetEnvironmentVariable(PasswordEnvVar);
        if (IsPlaceholder(password)) return connectionString;
        return ApplyPassword(provider, connectionString, password!);
    }

    private static string? HighestPrecedenceConnection(IConfiguration config, string provider)
    {
        if (config is IConfigurationRoot root)
        {
            foreach (var source in root.Providers.Reverse())
            {
                if (source.TryGet("Database:ConnectionString", out var cs) && !IsPlaceholder(cs))
                    return cs;
                if (source.TryGet("Database:Host", out var host) && !IsPlaceholder(host))
                    return BuildFromParts(config, provider);
            }
            return null;
        }
        // Non-root configs (test stubs): merged view.
        var mergedCs = config.GetValue<string>("Database:ConnectionString");
        if (!IsPlaceholder(mergedCs)) return mergedCs;
        var mergedHost = config.GetValue<string>("Database:Host");
        return IsPlaceholder(mergedHost) ? null : BuildFromParts(config, provider);
    }

    private static string DefaultConnectionString(string provider) =>
        provider == "mysql"
            ? "server=localhost;database=hathor;user=root;password=hathor"
            : "Host=localhost;Port=5432;Database=hathor;Username=postgres;Password=postgres";

    private static string? BuildFromParts(IConfiguration config, string provider)
    {
        var host = config.GetValue<string>("Database:Host");
        if (IsPlaceholder(host)) return null;
        if (provider == "mysql")
        {
            var user = config.GetValue<string>("Database:Username");
            var database = config.GetValue<string>("Database:Database");
            return new MySqlConnectionStringBuilder
            {
                Server = host,
                Port = config.GetValue<uint?>("Database:Port") ?? 3306,
                Database = IsPlaceholder(database) ? "hathor" : database,
                UserID = IsPlaceholder(user) ? "root" : user,
            }.ConnectionString;
        }
        else
        {
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
    }

    internal static string ApplyPassword(string provider, string connectionString, string password) =>
        provider == "mysql"
            ? new MySqlConnectionStringBuilder(connectionString) { Password = password }.ConnectionString
            : new NpgsqlConnectionStringBuilder(connectionString) { Password = password }.ConnectionString;

    // An unfilled secrets example must behave like "not configured".
    private static bool IsPlaceholder(string? value) =>
        string.IsNullOrWhiteSpace(value) || value.Contains("REPLACE_WITH_");
}

// One connection string drives both EF Core and Dapper (plan §2).
// Provider switch: Database:Provider = postgres (local default) | mysql (remote-compat/TiDB prod).
public sealed class DapperConnectionFactory(IConfiguration config)
{
    public string Provider { get; } = DatabaseConnection.Provider(config);

    public string ConnectionString { get; } = DatabaseConnection.Resolve(config);

    public bool IsMySql => Provider == "mysql";

    public IDbConnection Create()
    {
        if (IsMySql) return new MySqlConnection(ConnectionString);
        return new NpgsqlConnection(ConnectionString);
    }

    // ANSI-compatible identifier quoting per provider (plan: no RETURNING, LAST_INSERT_ID()).
    // Postgres folds unquoted identifiers to lowercase while EF creates
    // quoted PascalCase tables/columns, so Postgres SQL must quote; MySQL
    // keeps its historical unquoted shape (case-insensitive there).
    public string Quote(string identifier) => IsMySql ? $"`{identifier}`" : $"\"{identifier}\"";

    // EF stores Guids as uppercase CHAR on MySQL but as native uuid on
    // Postgres, where UPPER(uuid) is invalid — cast to text first.
    // The @UserId parameter stays the uppercase "D" form (see UserKey).
    public string UserIdPredicate(string? tableAlias = null)
    {
        if (IsMySql)
            return tableAlias is null
                ? "UPPER(UserId) = @UserId"
                : $"UPPER({tableAlias}.UserId) = @UserId";
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
        "Host=localhost;Port=5432;Database=hathor;Username=postgres;Password=postgres";
    public string StorageRoot { get; set; } = "data";
}
