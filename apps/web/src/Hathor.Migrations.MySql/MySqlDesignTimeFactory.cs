using Hathor.Infrastructure.Ef;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;
using Pomelo.EntityFrameworkCore.MySql.Infrastructure;

namespace Hathor.Migrations.MySql;

// Design-time host for `dotnet ef` scaffolding of THIS provider set.
// Fixed server version: scaffolding never connects (AutoDetect would).
public sealed class MySqlDesignTimeFactory : IDesignTimeDbContextFactory<HathorDbContext>
{
    public HathorDbContext CreateDbContext(string[] args)
    {
        var options = new DbContextOptionsBuilder<HathorDbContext>();
        options.UseMySql("server=localhost;database=hathor", new MySqlServerVersion(new Version(8, 0, 0)),
            x => x.MigrationsAssembly("Hathor.Migrations.MySql"));
        return new HathorDbContext(options.Options);
    }
}
