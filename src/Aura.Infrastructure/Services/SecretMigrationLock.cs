using Aura.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;

namespace Aura.Infrastructure.Services;

/// <summary>
/// Makes sure only one process runs the secret migration at a time. Several API replicas may all have
/// MIGRATE_SECRETS_TO_V2 set.
/// </summary>
public interface ISecretMigrationLock
{
    /// <summary>True when this caller now holds the lock. False means another process holds it.</summary>
    Task<bool> TryAcquireAsync(CancellationToken ct);

    Task ReleaseAsync(CancellationToken ct);
}

/// <summary>
/// Postgres session-level advisory lock. It is held by one connection, so the connection stays open from acquire
/// until release. Other providers have a single process to coordinate with, so they always acquire.
/// </summary>
public sealed class PostgresSecretMigrationLock : ISecretMigrationLock
{
    // Fixed key for this job only. Nothing else in the database takes advisory locks.
    private const long LockKey = 4_500_046;

    private readonly AuraDbContext _db;
    private bool _held;

    public PostgresSecretMigrationLock(AuraDbContext db)
    {
        _db = db;
    }

    public async Task<bool> TryAcquireAsync(CancellationToken ct)
    {
        if (!_db.Database.IsNpgsql())
            return true;

        // Keep the connection open. EF would otherwise return it to the pool between commands, and the lock
        // would be left on a connection nobody can unlock.
        await _db.Database.OpenConnectionAsync(ct);
        _held = await _db.Database.SqlQuery<bool>($"SELECT pg_try_advisory_lock({LockKey})").SingleAsync(ct);
        if (!_held)
            await _db.Database.CloseConnectionAsync();

        return _held;
    }

    public async Task ReleaseAsync(CancellationToken ct)
    {
        if (!_held)
            return;

        try
        {
            await _db.Database.SqlQuery<bool>($"SELECT pg_advisory_unlock({LockKey})").SingleAsync(ct);
        }
        finally
        {
            _held = false;
            await _db.Database.CloseConnectionAsync();
        }
    }
}
