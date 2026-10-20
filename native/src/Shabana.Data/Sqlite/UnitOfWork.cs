using System.Data;
using Dapper;
using Microsoft.Data.Sqlite;

namespace Shabana.Data.Sqlite;

/// <summary>
/// A connection plus its open transaction, handed to a write recipe. All SQL of a service goes through this (or through
/// <see cref="Connection"/> + <see cref="Transaction"/>) so that it runs inside the unit of work.
/// </summary>
public sealed class DbTx
{
    public DbTx(SqliteConnection connection, SqliteTransaction transaction)
    {
        Connection = connection;
        Transaction = transaction;
    }

    public SqliteConnection Connection { get; }

    public SqliteTransaction Transaction { get; }

    public int Execute(string sql, object? param = null) => Connection.Execute(sql, param, Transaction);

    public IEnumerable<T> Query<T>(string sql, object? param = null) => Connection.Query<T>(sql, param, Transaction);

    public T QuerySingle<T>(string sql, object? param = null) => Connection.QuerySingle<T>(sql, param, Transaction);

    public T? QueryFirstOrDefault<T>(string sql, object? param = null) => Connection.QueryFirstOrDefault<T>(sql, param, Transaction);

    public T? ExecuteScalar<T>(string sql, object? param = null) => Connection.ExecuteScalar<T>(sql, param, Transaction);

    /// <summary>
    /// Opens or closes the "admin edit window" (<c>app_context.editing</c>, recipes R5-R7 only: 03 section 9). The unit of work
    /// REFUSES to commit while it is still 1, so a recipe that forgets to close the window can never leave the guard triggers disabled.
    /// </summary>
    public void SetEditing(bool editing) =>
        Execute("UPDATE app_context SET editing = @e WHERE id = 1", new { e = editing ? 1 : 0 });
}

/// <summary>A connection with a deferred transaction: every read inside one call sees the same snapshot (04 section 5.3).</summary>
public sealed class DbRead
{
    public DbRead(SqliteConnection connection, SqliteTransaction transaction)
    {
        Connection = connection;
        Transaction = transaction;
    }

    public SqliteConnection Connection { get; }

    public SqliteTransaction Transaction { get; }

    public IEnumerable<T> Query<T>(string sql, object? param = null) => Connection.Query<T>(sql, param, Transaction);

    public T QuerySingle<T>(string sql, object? param = null) => Connection.QuerySingle<T>(sql, param, Transaction);

    public T? QueryFirstOrDefault<T>(string sql, object? param = null) => Connection.QueryFirstOrDefault<T>(sql, param, Transaction);

    public T? ExecuteScalar<T>(string sql, object? param = null) => Connection.ExecuteScalar<T>(sql, param, Transaction);
}

/// <summary>04 section 7.2: every business operation is ONE <c>BEGIN IMMEDIATE</c> transaction that also carries the actor.</summary>
public interface IUnitOfWork
{
    /// <summary>
    /// Write transaction: gate, open (pragmas), <c>BEGIN IMMEDIATE</c>, <c>UPDATE app_context SET actor_user_id = @actor, editing = 0</c>,
    /// run <paramref name="work"/>, COMMIT. Any exception rolls EVERYTHING back (including app_context) and propagates; a schema
    /// rule violation arrives as <see cref="Shabana.Core.Errors.DomainException"/>.
    /// </summary>
    Task<T> RunAsync<T>(string? actorUserId, Func<DbTx, Task<T>> work, CancellationToken cancellationToken = default);

    Task RunAsync(string? actorUserId, Func<DbTx, Task> work, CancellationToken cancellationToken = default);

    /// <summary>One consistent snapshot (deferred transaction, <c>query_only</c>). Never blocks, and is never blocked by, the writer.</summary>
    Task<T> ReadAsync<T>(Func<DbRead, Task<T>> work, CancellationToken cancellationToken = default);
}

/// <inheritdoc />
public sealed class UnitOfWork : IUnitOfWork
{
    // True while a RunAsync is executing in the current async flow: a nested write would wait for the gate it already holds.
    private static readonly AsyncLocal<bool> InsideWrite = new();

    private readonly ConnectionFactory _factory;
    private readonly SemaphoreSlim _gate = new(1, 1);

    public UnitOfWork(ConnectionFactory factory)
    {
        _factory = factory ?? throw new ArgumentNullException(nameof(factory));
    }

    public async Task RunAsync(string? actorUserId, Func<DbTx, Task> work, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(work);
        await RunAsync<object?>(actorUserId, async tx =>
        {
            await work(tx).ConfigureAwait(false);
            return null;
        }, cancellationToken).ConfigureAwait(false);
    }

    public async Task<T> RunAsync<T>(string? actorUserId, Func<DbTx, Task<T>> work, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(work);
        if (InsideWrite.Value)
        {
            throw new InvalidOperationException("A unit of work cannot be started from inside another one (it would wait for itself).");
        }

        cancellationToken.ThrowIfCancellationRequested();
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            InsideWrite.Value = true;
            return await RunLocked(actorUserId, work, cancellationToken).ConfigureAwait(false);
        }
        catch (SqliteException ex)
        {
            throw SqliteErrorMapper.ToException(ex);
        }
        finally
        {
            InsideWrite.Value = false;
            _gate.Release();
        }
    }

    private async Task<T> RunLocked<T>(string? actorUserId, Func<DbTx, Task<T>> work, CancellationToken cancellationToken)
    {
        // Never creates a file: only the migration runner creates the database. A file deleted under a running application is an error,
        // not a reason to start writing into a new blank one.
        using var connection = _factory.OpenExisting();
        using var transaction = connection.BeginTransaction(deferred: false);   // BEGIN IMMEDIATE
        try
        {
            var tx = new DbTx(connection, transaction);

            // Rolls back with the transaction, so a failed operation can never leave its actor behind (schema.sql header protocol).
            var touched = tx.Execute("UPDATE app_context SET actor_user_id = @uid, editing = 0 WHERE id = 1", new { uid = actorUserId });
            if (touched != 1)
            {
                throw new InvalidOperationException("app_context has no row: the database was not created by the migration runner.");
            }

            var result = await work(tx).ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();

            // Never commit with the admin edit window open: the guard triggers would stay disabled for the next operation.
            if (tx.ExecuteScalar<long>("SELECT editing FROM app_context WHERE id = 1") != 0)
            {
                throw new InvalidOperationException("The unit of work ended with app_context.editing = 1; the recipe must close the edit window.");
            }

            // Leave the committed row neutral: nothing outside a unit of work is attributed to this actor.
            tx.Execute("UPDATE app_context SET actor_user_id = NULL WHERE id = 1");
            transaction.Commit();
            return result;
        }
        catch
        {
            try
            {
                transaction.Rollback();
            }
            catch (Exception ex) when (ex is InvalidOperationException or SqliteException)
            {
                // Already rolled back by SQLite (e.g. SQLITE_FULL / IOERR): disposing the connection finishes the job.
            }

            throw;
        }
    }

    public async Task<T> ReadAsync<T>(Func<DbRead, Task<T>> work, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(work);
        cancellationToken.ThrowIfCancellationRequested();
        try
        {
            using var connection = _factory.OpenExisting();
            ConnectionFactory.Exec(connection, "PRAGMA query_only = ON");
            try
            {
                using var transaction = connection.BeginTransaction(deferred: true);
                try
                {
                    return await work(new DbRead(connection, transaction)).ConfigureAwait(false);
                }
                finally
                {
                    transaction.Rollback();   // nothing to commit
                }
            }
            finally
            {
                // Pooled connections go back to the pool: leave them writable (Open() also resets it).
                ConnectionFactory.Exec(connection, "PRAGMA query_only = OFF");
            }
        }
        catch (SqliteException ex)
        {
            throw SqliteErrorMapper.ToException(ex);
        }
    }
}
