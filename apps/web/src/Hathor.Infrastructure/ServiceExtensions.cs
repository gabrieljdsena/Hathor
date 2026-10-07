using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using FluentValidation;
using Hathor.Application.Ports;
using Hathor.Domain.Entities;
using Hathor.Domain.Repositories;
using Hathor.Infrastructure.Ef;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Metadata;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.EntityFrameworkCore.Migrations.Operations;
using Microsoft.EntityFrameworkCore.Storage;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.IdentityModel.Tokens;

namespace Hathor.Infrastructure.Auth;

// JWT signing-key gate: HMAC-SHA256 with a short key is forgeable, so keys
// under 32 bytes fail fast at startup instead of signing weak tokens.
public static class JwtKey
{
    public static string RequireValid(string? configured)
    {
        if (string.IsNullOrWhiteSpace(configured))
            throw new InvalidOperationException("JWT key is not configured.");
        if (System.Text.Encoding.UTF8.GetByteCount(configured) < 32)
            throw new InvalidOperationException(
                "JWT key must be at least 32 bytes (generate a long random string).");
        return configured;
    }
}

public sealed class JwtTokenService(IConfiguration config) : IJwtTokenService
{
    public string CreateAccessToken(Guid userId, string username, IEnumerable<string> scopes, Guid? sessionId = null)
    {
        var key = new SymmetricSecurityKey(
            Encoding.UTF8.GetBytes(JwtKey.RequireValid(config["Jwt:Key"]
                ?? Environment.GetEnvironmentVariable("JWT_KEY"))));
        var claims = new List<Claim>
        {
            new(ClaimTypes.NameIdentifier, userId.ToString()),
            new(ClaimTypes.Name, username),
            new(JwtRegisteredClaimNames.Jti, Guid.NewGuid().ToString()),
        };
        // Session the access token was minted for (sessions UI "current
        // device" marker). PATs have no session — claim simply absent.
        if (sessionId is not null)
            claims.Add(new Claim("sid", sessionId.Value.ToString()));
        // One claim per scope: RequireClaim("scope", "player:read") matches
        // claim values exactly, not space-separated blobs.
        foreach (var scope in scopes)
            claims.Add(new Claim("scope", scope));
        var token = new JwtSecurityToken(
            issuer: config["Jwt:Issuer"],
            audience: config["Jwt:Audience"],
            claims: claims,
            expires: DateTime.UtcNow.AddMinutes(15),
            signingCredentials: new SigningCredentials(key, SecurityAlgorithms.HmacSha256));
        return new JwtSecurityTokenHandler().WriteToken(token);
    }
}

// hth_<8-char-prefix><rest> tokens; only SHA256 hashes touch the database.
public sealed class ApiKeyService : IApiKeyService
{
    public int PrefixChars => 8;

    public (string Token, string Prefix, string Hash) Create()
    {
        var raw = Convert.ToBase64String(RandomNumberGenerator.GetBytes(48))
            .Replace("+", "").Replace("/", "").Replace("=", "");
        var prefix = raw[..8];
        return ($"hth_{raw}", prefix, Hash($"hth_{raw}"));
    }

    public string Hash(string token)
    {
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(token));
        return Convert.ToHexString(bytes).ToLowerInvariant();
    }
}

public static class InfrastructureServiceExtensions
{
    public static IServiceCollection AddHathorInfrastructure(
        this IServiceCollection services, IConfiguration config)
    {
        var provider = Hathor.Infrastructure.Dapper.DatabaseConnection.Provider(config);
        var connectionString = Hathor.Infrastructure.Dapper.DatabaseConnection.Resolve(config);

        services.AddDbContext<HathorDbContext>(options =>
        {
            if (provider == "mysql")
                options.UseMySql(connectionString, ServerVersion.AutoDetect(connectionString),
                    x => x.MigrationsAssembly("Hathor.Migrations.MySql"));
            else
                options.UseNpgsql(connectionString,
                    x => x.MigrationsAssembly("Hathor.Migrations.Postgres"));
        });

        var storageRoot = Path.GetFullPath(
            config.GetValue("Database:StorageRoot", "data") ?? "data");
        Directory.CreateDirectory(storageRoot);

        services.AddSingleton(new Dapper.DapperConnectionFactory(config));
        services.AddScoped<Domain.Repositories.IUserRepository, Repositories.EfUserRepository>();
        services.AddScoped<Domain.Repositories.IRefreshTokenRepository, Repositories.EfRefreshTokenRepository>();
        services.AddScoped<Domain.Repositories.ISessionRepository, Repositories.EfSessionRepository>();
        services.AddScoped<Domain.Repositories.IApiKeyRepository, Repositories.EfApiKeyRepository>();
        services.AddScoped<Domain.Repositories.IPlaybackStateRepository, Repositories.EfPlaybackStateRepository>();
        services.AddScoped<Domain.Repositories.IUserSettingsRepository, Repositories.EfUserSettingsRepository>();
        services.AddScoped<Domain.Repositories.ITombstoneRepository, Repositories.EfTombstoneRepository>();
        services.AddScoped<Domain.Repositories.IPlaylistRepository, Repositories.EfPlaylistRepository>();
        services.AddScoped<Domain.Repositories.IPodcastTagRepository, Repositories.EfPodcastTagRepository>();
        services.AddScoped<Domain.Repositories.ISongRecordRepository, Repositories.EfSongRecordRepository>();
        services.AddScoped<Domain.Repositories.IDailyMixRepository, Repositories.EfDailyMixRepository>();
        services.AddScoped<Domain.Repositories.IDiscoverCacheRepository, Repositories.EfDiscoverCacheRepository>();
        services.AddScoped<Domain.Repositories.IDownloadJobRepository, Repositories.EfDownloadJobRepository>();
        services.AddScoped<Domain.Repositories.ILyricsRepository, Repositories.EfLyricsRepository>();
        services.AddScoped<Domain.Repositories.IPodcastRecordRepository, Repositories.EfPodcastRecordRepository>();
        services.AddScoped<Domain.Repositories.IPodcastTimestampRepository, Repositories.EfPodcastTimestampRepository>();
        services.AddScoped<Domain.Repositories.IPendingEditRepository, Repositories.EfPendingEditRepository>();
        services.AddScoped<Application.Metadata.PendingMetadataApplier>();
        services.AddScoped<Application.Ports.IPodcastReadModel, Library.DapperPodcastReadModel>();
        services.AddSingleton<Application.Ports.ISystemProbe, Maintenance.SystemProbe>();
        services.AddSingleton<Maintenance.IFfmpegInstaller, Maintenance.FfmpegInstaller>();
        services.AddScoped<Application.Ports.ISyncService, Sync.EfSyncService>();
        services.AddScoped<Application.Ports.IRemotePullService>(sp =>
            new Sync.RemotePullService(
                sp.GetRequiredService<Application.Ports.ISyncService>(),
                sp.GetRequiredService<Application.Ingest.IDownloadQueue>(),
                sp.GetRequiredService<Application.Ports.ILibraryStorage>(),
                config.GetSection("RemoteDb").Get<Sync.RemoteDbOptions>() ?? new Sync.RemoteDbOptions(),
                sp.GetRequiredService<Microsoft.Extensions.Logging.ILogger<Sync.RemotePullService>>()));
        services.AddScoped<Application.Ports.IRemotePushService>(sp =>
            new Sync.RemotePushService(
                sp.GetRequiredService<Ef.HathorDbContext>(),
                config.GetSection("RemoteDb").Get<Sync.RemoteDbOptions>() ?? new Sync.RemoteDbOptions(),
                sp.GetRequiredService<Microsoft.Extensions.Logging.ILogger<Sync.RemotePushService>>()));
        services.AddScoped<Application.Ports.ISongReadModel, Library.DapperSongReadModel>();
        services.AddScoped<Application.Ports.IPlaylistReadModel, Library.DapperPlaylistReadModel>();
        services.AddScoped<Application.Ports.IPodcastTagReadModel, Library.DapperPodcastTagReadModel>();
        services.AddScoped<Application.Ports.IHistoryReadModel, Library.DapperHistoryReadModel>();
        services.AddScoped<Application.Ports.IDiscoverTasteReadModel, Library.DapperDiscoverTasteReadModel>();
        services.AddScoped<Application.Mix.DailyMixService>();
        services.AddScoped<Application.Discover.DiscoverService>();
        services.AddScoped<Application.Ports.ILibraryStorage>(_ => new Library.LocalLibraryStorage(
            storageRoot,
            config.GetValue("Library:SongsPath", ""),
            config.GetValue("Library:PodcastsPath", "")));
        services.AddSingleton<Library.SongMetadataCache>();
        services.AddScoped<Library.SongMetadataReader>(sp =>
            new Library.SongMetadataReader(
                sp.GetRequiredService<Application.Ports.ILibraryStorage>(),
                sp.GetRequiredService<Library.SongMetadataCache>()));
        services.AddHttpClient("metadata");
        services.AddScoped<Application.Ports.IMetadataWriter>(sp =>
            new Library.TagLibMetadataWriter(
                sp.GetRequiredService<Application.Ports.ILibraryStorage>(),
                sp.GetRequiredService<IHttpClientFactory>().CreateClient("metadata"),
                sp.GetRequiredService<Library.SongMetadataCache>()));
        services.AddSingleton<Application.Ports.IJwtTokenService, JwtTokenService>();
        services.AddSingleton<Application.Ports.IApiKeyService, ApiKeyService>();
        services.AddSingleton<Application.Ports.IDownloadEngine, Ingest.YoutubeExplodeEngine>();
        services.AddSingleton<Application.Ports.ILoudnessAnalyzer, Enrichment.LoudnessAnalyzer>();
        services.AddSingleton<Application.Ingest.IDownloadQueue, Ingest.DownloadQueueService>();
        services.AddScoped<Application.Ports.IITunesClient, Enrichment.ITunesClientImpl>();
        services.AddScoped<Application.Ports.IRomanizerBackend>(sp =>
            new Enrichment.FugashiRomanizer(
                config.GetSection("Romanize").Get<Enrichment.RomanizerOptions>()
                    ?? new Enrichment.RomanizerOptions(),
                sp.GetRequiredService<Microsoft.Extensions.Logging.ILogger<Enrichment.FugashiRomanizer>>()));
        services.AddScoped<Application.Ports.IDiscoverySuggester>(sp =>
            new Enrichment.LlamaDiscoverySuggester(
                sp.GetRequiredService<IHttpClientFactory>(),
                config.GetSection("Discovery").Get<Enrichment.DiscoveryLlmOptions>()
                    ?? new Enrichment.DiscoveryLlmOptions(),
                sp.GetRequiredService<Microsoft.Extensions.Logging.ILogger<Enrichment.LlamaDiscoverySuggester>>()));
        services.AddScoped<Application.Ports.ILrclibClient, Enrichment.LrclibClientImpl>();
        services.AddHttpClient("metadata");
        services.AddHttpClient("itunes");
        services.AddHttpClient("lrclib");
        services.AddHttpClient("llm");
        // FFmpeg static builds are ~80MB; the installer enforces its own
        // timeout, so the client itself never times out.
        services.AddHttpClient("ffmpeg").ConfigureHttpClient(c => c.Timeout = Timeout.InfiniteTimeSpan);
        services.AddHttpClient("nuget").ConfigureHttpClient(c => c.Timeout = TimeSpan.FromSeconds(15));
        services.AddMediatR(cfg => cfg.RegisterServicesFromAssembly(
            typeof(Application.Auth.LoginCommand).Assembly));
        services.AddValidatorsFromAssembly(
            typeof(Application.Auth.LoginCommand).Assembly);
        services.AddTransient(
            typeof(MediatR.IPipelineBehavior<,>),
            typeof(Application.ValidationBehavior<,>));

        return services;
    }

    public static void EnsureDatabaseCreated(HathorDbContext db) => EnsureMigrated(db);

    // Migration-framework startup: legacy EnsureCreated-era databases get a
    // baseline stamp, then Migrate() owns the schema from here on.
    public static void EnsureMigrated(HathorDbContext db)
    {
        // Serialize concurrent boots (double-started app, tests): without
        // this, two instances racing first-time setup collide on CREATE
        // DATABASE / extensions / migrations and one dies spuriously.
        using var _ = AcquireStartupLock(db);
        EnsurePostgresDatabaseExists(db);
        BaselineLegacyDatabase(db);
        db.Database.Migrate();
        EnsureDownloadTargetColumn(db);
        EnsurePgFuzzyExtensions(db);
    }

    // Session-level advisory lock on the maintenance database: held (open
    // connection) for the whole migration. Best effort — if locking fails,
    // the individual steps below are duplicate-tolerant anyway.
    private static IDisposable? AcquireStartupLock(HathorDbContext db)
    {
        if (db.Database.IsMySql()) return null;
        try
        {
            var connectionString = db.Database.GetConnectionString();
            if (string.IsNullOrWhiteSpace(connectionString)) return null;
            var builder = new Npgsql.NpgsqlConnectionStringBuilder(connectionString)
            {
                Database = "postgres",
            };
            var conn = new Npgsql.NpgsqlConnection(builder.ConnectionString);
            conn.Open();
            using var cmd = new Npgsql.NpgsqlCommand(
                "SELECT pg_advisory_lock(7272718193616933103)", conn);
            cmd.ExecuteNonQuery();
            return conn;
        }
        catch
        {
            return null;
        }
    }

    // A fresh native Postgres has no `hathor` database yet (migrations
    // create tables, not databases): create it on first boot via the
    // maintenance database. Wrong passwords still fail loudly below.
    private static void EnsurePostgresDatabaseExists(HathorDbContext db)
    {
        if (db.Database.IsMySql()) return;
        var connectionString = db.Database.GetConnectionString();
        if (string.IsNullOrWhiteSpace(connectionString)) return;
        var builder = new Npgsql.NpgsqlConnectionStringBuilder(connectionString);
        var database = builder.Database;
        if (string.IsNullOrWhiteSpace(database)) return;
        try
        {
            using var probe = new Npgsql.NpgsqlConnection(connectionString);
            probe.Open();
            return; // Exists and reachable.
        }
        catch (Npgsql.PostgresException ex) when (ex.SqlState == Npgsql.PostgresErrorCodes.InvalidCatalogName)
        {
            // Database missing — fall through and create it.
        }
        builder.Database = "postgres";
        using var admin = new Npgsql.NpgsqlConnection(builder.ConnectionString);
        admin.Open();
        try
        {
            using var create = new Npgsql.NpgsqlCommand(
                $"CREATE DATABASE {QuoteIdent(database)}", admin);
            create.ExecuteNonQuery();
        }
        catch (Npgsql.PostgresException ex)
            when (ex.SqlState is Npgsql.PostgresErrorCodes.DuplicateDatabase
                or "23505")
        {
            // Lost a CREATE race with another boot — the database exists now.
        }
    }

    private static string QuoteIdent(string identifier) =>
        "\"" + identifier.Replace("\"", "\"\"") + "\"";

    // pg_trgm + fuzzystrmatch back DB-level typo-tolerant search (GIN
    // trigram indexes on future migrations). Best effort: the app's fuzzy
    // search runs in-memory regardless, so managed databases without
    // extension privileges still work.
    private static void EnsurePgFuzzyExtensions(HathorDbContext db)
    {
        if (db.Database.IsMySql()) return;
        try
        {
            // DO-block (not bare CREATE EXTENSION): concurrent boots racing
            // the same statement would otherwise log duplicate_object errors.
            db.Database.ExecuteSqlRaw(
                "DO $$ BEGIN CREATE EXTENSION IF NOT EXISTS pg_trgm; " +
                "EXCEPTION WHEN duplicate_object THEN NULL; END $$");
            db.Database.ExecuteSqlRaw(
                "DO $$ BEGIN CREATE EXTENSION IF NOT EXISTS fuzzystrmatch; " +
                "EXCEPTION WHEN duplicate_object THEN NULL; END $$");
        }
        catch
        {
            // Missing privileges — in-memory fuzzy search covers it.
        }
    }

    // Databases created before migrations have tables but no history —
    // Migrate() would replay InitialCreate onto existing tables and fail.
    // History is judged by ROWS, not the table: a failed Migrate() leaves an
    // empty history table behind, and trusting the table alone would skip
    // the bridge forever.
    //
    // Fresh databases (no application tables at all) skip the bridge:
    // Migrate() owns the schema entirely. Legacy databases (tables, partial
    // or no history) and poisoned ones (history, missing tables) get their
    // missing tables AND columns created from the current model, then every
    // migration the bridge already built is stamped — so Migrate() applies
    // only genuinely pending migrations and never replays built ones (the
    // duplicate-column failure that motivated this shape).
    private static void BaselineLegacyDatabase(HathorDbContext db)
    {
        try
        {
            var migrations = db.GetService<IMigrationsAssembly>().Migrations.Keys
                .OrderBy(k => k, StringComparer.Ordinal).ToList();
            if (migrations.Count == 0) return;
            var applied = GetAppliedMigrations(db);
            var existing = GetTableNames(db);
            var designModel = GetDesignModel(db);
            var modelTables = designModel?.GetEntityTypes()
                    .Select(e => e.GetTableName())
                    .OfType<string>()
                    .ToHashSet(StringComparer.OrdinalIgnoreCase)
                ?? new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            if (!modelTables.Any(existing.Contains)) return; // fresh database
            CreateMissingTables(db, designModel);
            AddMissingColumns(db, designModel);
            foreach (var migration in migrations.Where(m => !applied.Contains(m)))
                StampMigration(db, migration);
        }
        catch
        {
            // Best effort: Migrate() below surfaces genuine problems loudly.
        }
    }

    private static void StampMigration(HathorDbContext db, string initial)
    {
        var version = typeof(DbContext).Assembly.GetName().Version?.ToString() ?? "9.0.0";
        if (db.Database.IsMySql())
        {
            db.Database.ExecuteSqlRaw(
                "CREATE TABLE IF NOT EXISTS `__EFMigrationsHistory` " +
                "(`MigrationId` varchar(150) NOT NULL, `ProductVersion` varchar(32) NOT NULL, " +
                "PRIMARY KEY (`MigrationId`))");
            db.Database.ExecuteSqlRaw(
                "INSERT IGNORE INTO `__EFMigrationsHistory` (`MigrationId`, `ProductVersion`) VALUES ({0}, {1})",
                initial, version);
        }
        else
        {
            db.Database.ExecuteSqlRaw(
                "CREATE TABLE IF NOT EXISTS \"__EFMigrationsHistory\" " +
                "(\"MigrationId\" character varying(150) NOT NULL, " +
                "\"ProductVersion\" character varying(32) NOT NULL, " +
                "CONSTRAINT \"PK___EFMigrationsHistory\" PRIMARY KEY (\"MigrationId\"))");
            db.Database.ExecuteSqlRaw(
                "INSERT INTO \"__EFMigrationsHistory\" (\"MigrationId\", \"ProductVersion\") " +
                "VALUES ({0}, {1}) ON CONFLICT DO NOTHING",
                initial, version);
        }
    }

    private static HashSet<string> GetAppliedMigrations(HathorDbContext db)
    {
        var applied = new HashSet<string>(StringComparer.Ordinal);
        try
        {
            var conn = db.Database.GetDbConnection();
            var wasOpen = conn.State == System.Data.ConnectionState.Open;
            if (!wasOpen) conn.Open();
            try
            {
                using var cmd = conn.CreateCommand();
                if (db.Database.IsMySql())
                    cmd.CommandText = "SELECT `MigrationId` FROM `__EFMigrationsHistory`";
                else
                    cmd.CommandText = "SELECT \"MigrationId\" FROM \"__EFMigrationsHistory\"";
                using var reader = cmd.ExecuteReader();
                while (reader.Read()) applied.Add(reader.GetString(0));
            }
            finally
            {
                if (!wasOpen) conn.Close();
            }
        }
        catch
        {
            // Missing table → nothing applied (fresh/legacy database).
        }
        return applied;
    }

    // RelationalDatabaseCreator.CreateTables() aborts on the first existing
    // table, so bridge table-by-table: every CreateTable (+ its indexes)
    // for tables the database does not have yet. Provider-agnostic — the
    // SQL comes from each provider's own generator.
    private static void CreateMissingTables(
        HathorDbContext db, Microsoft.EntityFrameworkCore.Metadata.IModel? designModel)
    {
        if (designModel is null) return;
        var differ = db.GetService<IMigrationsModelDiffer>();
        var generator = db.GetService<IMigrationsSqlGenerator>();
        var existing = GetTableNames(db);
        var ops = differ.GetDifferences(null, designModel.GetRelationalModel());
        var missing = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var op in ops.OfType<CreateTableOperation>())
            if (!existing.Contains(op.Name))
                missing.Add(op.Name);
        if (missing.Count == 0) return;
        var wanted = ops.Where(op =>
            (op is CreateTableOperation ct && missing.Contains(ct.Name)) ||
            (op is CreateIndexOperation ci && missing.Contains(ci.Table))).ToList();
        if (wanted.Count == 0) return;

        // DDL only — CreateTable/CreateIndex carry no parameters.
        ExecuteCommands(db, generator.Generate(wanted, designModel));
    }

    // Columns added by later migrations are missing on legacy tables the
    // same way whole tables are: add every model column the table does not
    // have yet (provider SQL, same as tables above).
    private static void AddMissingColumns(
        HathorDbContext db, Microsoft.EntityFrameworkCore.Metadata.IModel? designModel)
    {
        if (designModel is null) return;
        var generator = db.GetService<IMigrationsSqlGenerator>();
        foreach (var entityType in designModel.GetEntityTypes())
        {
            var table = entityType.GetTableName();
            if (table is null) continue;
            var schema = entityType.GetSchema();
            var id = Microsoft.EntityFrameworkCore.Metadata.StoreObjectIdentifier.Table(table, schema);
            var existing = GetColumnNames(db, table);
            var ops = new List<MigrationOperation>();
            foreach (var prop in entityType.GetProperties())
            {
                var column = prop.GetColumnName(id);
                if (column is null || existing.Contains(column)) continue;
                ops.Add(new AddColumnOperation
                {
                    Table = table,
                    Schema = schema,
                    Name = column,
                    ClrType = prop.ClrType,
                    ColumnType = prop.GetColumnType(id),
                    IsNullable = prop.IsNullable,
                    DefaultValue = prop.GetDefaultValue(id),
                    DefaultValueSql = prop.GetDefaultValueSql(id),
                    ComputedColumnSql = prop.GetComputedColumnSql(id),
                });
            }
            if (ops.Count > 0)
                ExecuteCommands(db, generator.Generate(ops, designModel));
        }
    }

    private static void ExecuteCommands(
        HathorDbContext db, IReadOnlyList<MigrationCommand> commands)
    {
        var conn = db.Database.GetDbConnection();
        var wasOpen = conn.State == System.Data.ConnectionState.Open;
        if (!wasOpen) conn.Open();
        try
        {
            foreach (var cmd in commands)
            {
                using var command = conn.CreateCommand();
                command.CommandText = cmd.CommandText;
                command.ExecuteNonQuery();
            }
        }
        finally
        {
            if (!wasOpen) conn.Close();
        }
    }

    // Design model comes from the provider's model snapshot (the runtime
    // model is read-optimized and unusable here), initialized for
    // relational access first.
    private static Microsoft.EntityFrameworkCore.Metadata.IModel? GetDesignModel(HathorDbContext db)
    {
        var designModel = db.GetService<IMigrationsAssembly>().ModelSnapshot?.Model;
        if (designModel is null) return null;
        return db.GetService<IModelRuntimeInitializer>().Initialize(designModel);
    }

    private static HashSet<string> GetTableNames(HathorDbContext db)
    {
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        try
        {
            var conn = db.Database.GetDbConnection();
            var wasOpen = conn.State == System.Data.ConnectionState.Open;
            if (!wasOpen) conn.Open();
            try
            {
                using var cmd = conn.CreateCommand();
                if (db.Database.IsMySql())
                    cmd.CommandText = "SELECT table_name FROM information_schema.tables " +
                        "WHERE table_schema = DATABASE() AND table_type = 'BASE TABLE'";
                else
                    cmd.CommandText = "SELECT table_name FROM information_schema.tables " +
                        "WHERE table_schema = current_schema() AND table_type = 'BASE TABLE'";
                using var reader = cmd.ExecuteReader();
                while (reader.Read()) names.Add(reader.GetString(0));
            }
            finally
            {
                if (!wasOpen) conn.Close();
            }
        }
        catch
        {
            // Unknown — CreateMissingTables proceeds table-by-table below.
        }
        return names;
    }

    private static HashSet<string> GetColumnNames(HathorDbContext db, string table)
    {
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        try
        {
            var conn = db.Database.GetDbConnection();
            var wasOpen = conn.State == System.Data.ConnectionState.Open;
            if (!wasOpen) conn.Open();
            try
            {
                using var cmd = conn.CreateCommand();
                if (db.Database.IsMySql())
                    cmd.CommandText = "SELECT column_name FROM information_schema.columns " +
                        "WHERE table_schema = DATABASE() AND table_name = @t";
                else
                    cmd.CommandText = "SELECT column_name FROM information_schema.columns " +
                        "WHERE table_schema = current_schema() AND table_name = @t";
                var par = cmd.CreateParameter();
                par.ParameterName = "@t";
                par.Value = table;
                cmd.Parameters.Add(par);
                using var reader = cmd.ExecuteReader();
                while (reader.Read()) names.Add(reader.GetString(0));
            }
            finally
            {
                if (!wasOpen) conn.Close();
            }
        }
        catch
        {
            // Unknown — AddMissingColumns may attempt an add that collides;
            // Migrate() below surfaces genuine problems loudly.
        }
        return names;
    }

    // EnsureCreated never alters existing databases, so columns added later
    // need an explicit ensure (both providers).
    private static void EnsureDownloadTargetColumn(HathorDbContext db)
    {
        try
        {
            var conn = db.Database.GetDbConnection();
            var wasOpen = conn.State == System.Data.ConnectionState.Open;
            if (!wasOpen) conn.Open();
            try
            {
                if (db.Database.IsMySql())
                {
                    using var check = conn.CreateCommand();
                    check.CommandText =
                        "SELECT COUNT(*) FROM information_schema.columns " +
                        "WHERE table_schema = DATABASE() AND table_name = 'Download_Queue' " +
                        "AND column_name = 'TargetFile'";
                    if (Convert.ToInt64(check.ExecuteScalar()) == 0)
                    {
                        using var alter = conn.CreateCommand();
                        alter.CommandText =
                            "ALTER TABLE `Download_Queue` ADD COLUMN `TargetFile` TEXT NULL";
                        alter.ExecuteNonQuery();
                    }
                }
                else
                {
                    using var check = conn.CreateCommand();
                    check.CommandText =
                        "SELECT COUNT(*) FROM information_schema.columns " +
                        "WHERE table_schema = current_schema() AND table_name = 'Download_Queue' " +
                        "AND column_name = 'TargetFile'";
                    if (Convert.ToInt64(check.ExecuteScalar()) == 0)
                    {
                        using var alter = conn.CreateCommand();
                        alter.CommandText =
                            "ALTER TABLE \"Download_Queue\" ADD COLUMN \"TargetFile\" text NULL";
                        alter.ExecuteNonQuery();
                    }
                }
            }
            finally
            {
                if (!wasOpen) conn.Close();
            }
        }
        catch
        {
            // Best effort for pre-existing databases; fresh ones already
            // have the column from EnsureCreated.
        }
    }
}
