using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace Aura.Infrastructure.Data;

public class AuraDbContextFactory : IDesignTimeDbContextFactory<AuraDbContext>
{
    public AuraDbContext CreateDbContext(string[] args)
    {
        var optionsBuilder = new DbContextOptionsBuilder<AuraDbContext>();
        var connectionString = Environment.GetEnvironmentVariable("DATABASE_URL")
            ?? "Host=localhost;Database=aura;Username=aura;Password=changeme";
        optionsBuilder.UseNpgsql(connectionString);
        return new AuraDbContext(optionsBuilder.Options);
    }
}
