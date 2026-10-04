using Hathor.Infrastructure.Ef;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace Hathor.Application.Tests;

// Ephemeral Postgres databases for handler tests (real SQL incl.
// ExecuteDelete, unlike the InMemory provider). Requires a reachable
// Postgres superuser connection: HATHOR_TEST_PG, e.g.
// "Host=localhost;Port=5433;Username=postgres;Password=postgres;Database=postgres".
public static class TestPostgres
{
    public static string AdminConnectionString =>
        Environment.GetEnvironmentVariable("HATHOR_TEST_PG")
        ?? "Host=localhost;Port=5432;Username=postgres;Password=postgres;Database=postgres";

    public static async Task<(string ConnectionString, string Database)> CreateDatabaseAsync(string prefix)
    {
        var name = $"{prefix}_{Guid.NewGuid():N}";
        await using var conn = new NpgsqlConnection(AdminConnectionString);
        await conn.OpenAsync();
        await using var create = new NpgsqlCommand($"CREATE DATABASE \"{name}\"", conn);
        await create.ExecuteNonQueryAsync();
        var builder = new NpgsqlConnectionStringBuilder(AdminConnectionString) { Database = name };
        return (builder.ToString(), name);
    }

    public static async Task DropDatabaseAsync(string database)
    {
        await using var conn = new NpgsqlConnection(AdminConnectionString);
        await conn.OpenAsync();
        await using var kill = new NpgsqlCommand(
            "SELECT pg_terminate_backend(pid) FROM pg_stat_activity " +
            "WHERE datname = @db AND pid <> pg_backend_pid()", conn);
        kill.Parameters.AddWithValue("db", database);
        await kill.ExecuteNonQueryAsync();
        await using var drop = new NpgsqlCommand($"DROP DATABASE IF EXISTS \"{database}\"", conn);
        await drop.ExecuteNonQueryAsync();
    }

    public static DbContextOptions<HathorDbContext> Options(string connectionString) =>
        new DbContextOptionsBuilder<HathorDbContext>()
            .UseNpgsql(connectionString, x => x.MigrationsAssembly("Hathor.Migrations.Postgres"))
            .Options;
}
