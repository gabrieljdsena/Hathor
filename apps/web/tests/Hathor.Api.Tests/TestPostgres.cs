using Hathor.Infrastructure.Ef;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace Hathor.Api.Tests;

// Ephemeral Postgres databases for API tests (LibraryFixture gives every
// test a fresh migrated database). Requires a reachable Postgres
// superuser connection: HATHOR_TEST_PG, e.g.
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

    public static async Task<List<string>> ColumnNamesAsync(string connectionString, string table)
    {
        await using var conn = new NpgsqlConnection(connectionString);
        await conn.OpenAsync();
        await using var cmd = new NpgsqlCommand(
            "SELECT column_name FROM information_schema.columns " +
            "WHERE table_schema = current_schema() AND table_name = @t ORDER BY ordinal_position", conn);
        cmd.Parameters.AddWithValue("t", table);
        await using var reader = await cmd.ExecuteReaderAsync();
        var cols = new List<string>();
        while (await reader.ReadAsync()) cols.Add(reader.GetString(0));
        return cols;
    }

    public static async Task ExecuteAsync(string connectionString, string sql)
    {
        await using var conn = new NpgsqlConnection(connectionString);
        await conn.OpenAsync();
        await using var cmd = new NpgsqlCommand(sql, conn);
        await cmd.ExecuteNonQueryAsync();
    }

    public static async Task<long> CountAsync(string connectionString, string sql)
    {
        await using var conn = new NpgsqlConnection(connectionString);
        await conn.OpenAsync();
        await using var cmd = new NpgsqlCommand(sql, conn);
        return Convert.ToInt64(await cmd.ExecuteScalarAsync());
    }

    public static async Task<string?> TextAsync(string connectionString, string sql)
    {
        await using var conn = new NpgsqlConnection(connectionString);
        await conn.OpenAsync();
        await using var cmd = new NpgsqlCommand(sql, conn);
        return await cmd.ExecuteScalarAsync() as string;
    }

    public static async Task<List<string>> TextsAsync(string connectionString, string sql)
    {
        await using var conn = new NpgsqlConnection(connectionString);
        await conn.OpenAsync();
        await using var cmd = new NpgsqlCommand(sql, conn);
        await using var reader = await cmd.ExecuteReaderAsync();
        var values = new List<string>();
        while (await reader.ReadAsync()) values.Add(reader.GetString(0));
        return values;
    }
}
