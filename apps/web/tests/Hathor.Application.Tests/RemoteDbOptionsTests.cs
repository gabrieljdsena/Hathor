using FluentAssertions;
using Hathor.Infrastructure.Sync;

namespace Hathor.Application.Tests;

// Sync-remote address guard: the bare DB_* env names are shared with
// myhomelab's Postgres, so a :5432 remote is a MySQL handshake against
// Postgres (minute-long timeout per push). Pure string check, no DB.
public sealed class RemoteDbOptionsTests
{
    [Theory]
    [InlineData("Server=localhost;Port=5432;User ID=u;Password=p;Database=d", true)]
    [InlineData("Server=gateway01.tidbcloud.com;Port=4000;User ID=u;Password=p;Database=d", false)]
    [InlineData("Server=db;Port=3306;User ID=u;Password=p;Database=d", false)]
    [InlineData(null, false)]
    [InlineData("", false)]
    [InlineData("not a connection string", false)]
    public void IsPostgresPort_FlagsOnly5432(string? connectionString, bool expected) =>
        RemoteDbOptions.IsPostgresPort(connectionString).Should().Be(expected);
}
