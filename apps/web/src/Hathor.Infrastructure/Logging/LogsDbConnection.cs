using Microsoft.Extensions.Configuration;
using Npgsql;

namespace Hathor.Infrastructure.Logging;

// Resolves the shared lab-Postgres connection used by PostgresLogSink.
// The full connection string ("Logging:ConnectionString") wins when set;
// otherwise it is built from the individual "Logging" parts below
// (Host/Port/Database/Username). The password always comes from the
// gitignored appsettings.Secrets.json ("Logging:Password") or the
// HATHOR_LOGS_DB_PASSWORD environment variable — same split myhomelab
// uses (MYHOMELAB_DB_PASSWORD).
public static class LogsDbConnection
{
    public const string PasswordEnvVar = "HATHOR_LOGS_DB_PASSWORD";

    public static string? Resolve(IConfiguration config)
    {
        var connectionString = config.GetValue<string>("Logging:ConnectionString");
        if (IsPlaceholder(connectionString))
            connectionString = BuildFromParts(config);
        if (string.IsNullOrWhiteSpace(connectionString)) return null;

        // Same whitespace rule as the main database password: an empty
        // appsettings value must not block the environment fallback.
        var password = config.GetValue<string>("Logging:Password");
        if (string.IsNullOrWhiteSpace(password))
            password = Environment.GetEnvironmentVariable(PasswordEnvVar);
        if (string.IsNullOrWhiteSpace(password)) return connectionString;

        return new NpgsqlConnectionStringBuilder(connectionString)
        {
            Password = password,
        }.ConnectionString;
    }

    private static string? BuildFromParts(IConfiguration config)
    {
        var host = config.GetValue<string>("Logging:Host");
        if (IsPlaceholder(host)) return null;
        var database = config.GetValue<string>("Logging:Database");
        var username = config.GetValue<string>("Logging:Username");
        return new NpgsqlConnectionStringBuilder
        {
            Host = host,
            Port = config.GetValue<int?>("Logging:Port") ?? 5432,
            Database = IsPlaceholder(database) ? "myhomelab" : database,
            Username = IsPlaceholder(username) ? "postgres" : username,
        }.ConnectionString;
    }

    // An unfilled copy of appsettings.Secrets.example.json must behave
    // like "not configured" (sink stays a no-op), never a bogus host.
    private static bool IsPlaceholder(string? value) =>
        string.IsNullOrWhiteSpace(value) || value.Contains("REPLACE_WITH_");
}
