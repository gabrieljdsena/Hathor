using System.Threading.Channels;
using FluentAssertions;
using Hathor.Infrastructure.Logging;
using Microsoft.Extensions.Configuration;
using Serilog.Events;
using Serilog.Parsing;

namespace Hathor.Api.Tests;

public sealed class PostgresLogSinkTests
{
    private static readonly DateTimeOffset Timestamp =
        new(2026, 10, 4, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public void FormatMessage_Without_Exception()
    {
        var text = PostgresLogSink.FormatMessage(Timestamp, "Error", "boom", null);
        text.Should().Be("2026-10-04 12:00:00 +00:00 [Error] boom");
    }

    [Fact]
    public void FormatMessage_Appends_Exception_On_Next_Line()
    {
        var text = PostgresLogSink.FormatMessage(Timestamp, "Fatal", "boom", "System.Exception: bang");
        text.Should().Be($"2026-10-04 12:00:00 +00:00 [Fatal] boom{Environment.NewLine}System.Exception: bang");
    }

    [Fact]
    public void FormatMessage_Uses_LocalWallTime_With_Offset()
    {
        var local = new DateTimeOffset(2026, 10, 4, 11, 8, 7, TimeSpan.FromHours(-3));
        var text = PostgresLogSink.FormatMessage(local, "Error", "boom", null);
        text.Should().Be("2026-10-04 11:08:07 -03:00 [Error] boom");
    }

    [Fact]
    public void LogsDbConnection_EmptyConnectionString_ResolvesNull()
    {
        var config = new StubConfig(new Dictionary<string, string?> { ["Logging:ConnectionString"] = "" });
        LogsDbConnection.Resolve(config).Should().BeNull();
    }

    [Fact]
    public void LogsDbConnection_PasswordOverride_Applies()
    {
        var config = new StubConfig(new Dictionary<string, string?>
        {
            ["Logging:ConnectionString"] = "Host=localhost;Database=myhomelab;Username=postgres",
            ["Logging:Password"] = "s3cret",
        });
        LogsDbConnection.Resolve(config).Should().Contain("Password=s3cret");
    }

    [Fact]
    public void LogsDbConnection_EmptyConnectionString_BuildsFromParts()
    {
        var config = new StubConfig(new Dictionary<string, string?>
        {
            ["Logging:ConnectionString"] = "",
            ["Logging:Host"] = "dbhost",
            ["Logging:Port"] = "5433",
            ["Logging:Database"] = "labdb",
            ["Logging:Username"] = "labuser",
            ["Logging:Password"] = "s3cret",
        });
        var resolved = LogsDbConnection.Resolve(config);
        resolved.Should().Contain("Host=dbhost");
        resolved.Should().Contain("Port=5433");
        resolved.Should().Contain("Database=labdb");
        resolved.Should().Contain("Username=labuser");
        resolved.Should().Contain("Password=s3cret");
    }

    [Fact]
    public void LogsDbConnection_EmptyPassword_FallsBackToEnvironment()
    {
        var previous = Environment.GetEnvironmentVariable(LogsDbConnection.PasswordEnvVar);
        try
        {
            Environment.SetEnvironmentVariable(LogsDbConnection.PasswordEnvVar, "env-secret");
            var config = new StubConfig(new Dictionary<string, string?>
            {
                ["Logging:ConnectionString"] = "Host=localhost;Database=myhomelab;Username=postgres",
                ["Logging:Password"] = "",
            });
            LogsDbConnection.Resolve(config).Should().Contain("Password=env-secret");
        }
        finally
        {
            Environment.SetEnvironmentVariable(LogsDbConnection.PasswordEnvVar, previous);
        }
    }

    [Fact]
    public void LogsDbConnection_UnfilledPlaceholders_ResolveNull()
    {
        var config = new StubConfig(new Dictionary<string, string?>
        {
            ["Logging:ConnectionString"] = "",
            ["Logging:Host"] = "REPLACE_WITH_LAB_POSTGRES_HOST",
            ["Logging:Password"] = "REPLACE_WITH_LAB_POSTGRES_PASSWORD",
        });
        LogsDbConnection.Resolve(config).Should().BeNull();
    }
}