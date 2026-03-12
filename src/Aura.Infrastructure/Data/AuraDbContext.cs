using Aura.Core.Entities;
using Aura.Core.Interfaces;
using Microsoft.EntityFrameworkCore;

namespace Aura.Infrastructure.Data;

public class AuraDbContext : DbContext
{
    private readonly Guid _tenantId;

    public AuraDbContext(DbContextOptions<AuraDbContext> options, ITenantContext tenantContext)
        : base(options)
    {
        _tenantId = tenantContext.TenantId;
    }

    public AuraDbContext(DbContextOptions<AuraDbContext> options)
        : base(options)
    {
        _tenantId = Guid.Empty;
    }

    public DbSet<Tenant> Tenants => Set<Tenant>();
    public DbSet<User> Users => Set<User>();
    public DbSet<CloudAccount> CloudAccounts => Set<CloudAccount>();
    public DbSet<Essence> Essences => Set<Essence>();
    public DbSet<Deployment> Deployments => Set<Deployment>();
    public DbSet<DeploymentRun> DeploymentRuns => Set<DeploymentRun>();
    public DbSet<DeploymentLayer> DeploymentLayers => Set<DeploymentLayer>();
    public DbSet<AuditLogEntry> AuditLog => Set<AuditLogEntry>();
    public DbSet<EssenceVersion> EssenceVersions => Set<EssenceVersion>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        // Tenant query filters
        modelBuilder.Entity<User>().HasQueryFilter(e => e.TenantId == _tenantId);
        modelBuilder.Entity<CloudAccount>().HasQueryFilter(e => e.TenantId == _tenantId);
        modelBuilder.Entity<Essence>().HasQueryFilter(e => e.TenantId == _tenantId);
        modelBuilder.Entity<Deployment>().HasQueryFilter(e => e.TenantId == _tenantId);
        modelBuilder.Entity<DeploymentRun>().HasQueryFilter(e => e.TenantId == _tenantId);

        // Tenant
        modelBuilder.Entity<Tenant>(b =>
        {
            b.HasIndex(t => t.Slug).IsUnique();
        });

        // User
        modelBuilder.Entity<User>(b =>
        {
            b.HasIndex(u => new { u.TenantId, u.Email }).IsUnique();
            b.Property(u => u.Role).HasConversion<string>();
        });

        // CloudAccount
        modelBuilder.Entity<CloudAccount>(b =>
        {
            b.Property(c => c.Provider).HasConversion<string>();
        });

        // Essence
        modelBuilder.Entity<Essence>(b =>
        {
            b.Property(e => e.EssenceJson).HasColumnType("jsonb");
        });

        // DeploymentRun
        modelBuilder.Entity<DeploymentRun>(b =>
        {
            b.Property(r => r.SnapshotJson).HasColumnType("jsonb");
            b.Property(r => r.Status).HasConversion<string>();
        });

        // AuditLogEntry
        modelBuilder.Entity<AuditLogEntry>(b =>
        {
            b.HasIndex(a => new { a.TenantId, a.CreatedAt });
            b.HasIndex(a => new { a.TenantId, a.EntityType, a.EntityId });
        });

        // EssenceVersion
        modelBuilder.Entity<EssenceVersion>(b =>
        {
            b.Property(v => v.EssenceJson).HasColumnType("jsonb");
            b.HasIndex(v => new { v.EssenceId, v.VersionNumber }).IsUnique();
        });

        // DeploymentLayer
        modelBuilder.Entity<DeploymentLayer>(b =>
        {
            b.Property(l => l.Parameters).HasColumnType("jsonb");
            b.Property(l => l.DependsOn).HasColumnType("jsonb");
            b.Property(l => l.ExecutorType).HasConversion<string>();
            b.Property(l => l.Status).HasConversion<string>();
        });
    }

    public override Task<int> SaveChangesAsync(CancellationToken cancellationToken = default)
    {
        foreach (var entry in ChangeTracker.Entries<BaseEntity>()
            .Where(e => e.State == EntityState.Added))
        {
            entry.Entity.CreatedAt = DateTime.UtcNow;
            if (entry.Entity.Id == Guid.Empty)
                entry.Entity.Id = Guid.NewGuid();
        }

        return base.SaveChangesAsync(cancellationToken);
    }
}
