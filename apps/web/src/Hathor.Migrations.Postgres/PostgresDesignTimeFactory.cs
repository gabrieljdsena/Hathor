using Hathor.Infrastructure.Ef;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace Hathor.Migrations.Postgres;

// Design-time host for `dotnet ef` scaffolding of THIS provider set.
public sealed class PostgresDesignTimeFactory : IDesignTimeDbContextFactory<HathorDbContext>
{
    public HathorDbContext CreateDbContext(string[] args)
    {
        var options = new DbContextOptionsBuilder<HathorDbContext>();
        options.UseNpgsql("Host=localhost;Database=hathor",
            x => x.MigrationsAssembly("Hathor.Migrations.Postgres"));
        return new HathorDbContext(options.Options);
    }
}
