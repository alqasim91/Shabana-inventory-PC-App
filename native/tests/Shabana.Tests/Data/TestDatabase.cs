using Shabana.Data.Backup;
using Shabana.Data.Migrations;
using Shabana.Data.Sqlite;

namespace Shabana.Tests.Data;

/// <summary>A fresh file database created by the real <see cref="MigrationRunner"/> from the EMBEDDED baseline, plus its factory and unit of work.</summary>
internal sealed class TestDatabase
{
    private TestDatabase(string path, ConnectionFactory factory)
    {
        Path = path;
        Factory = factory;
        Uow = new UnitOfWork(factory);
    }

    public string Path { get; }

    public ConnectionFactory Factory { get; }

    public UnitOfWork Uow { get; }

    public static TestDatabase Create(TempDir dir, ConnectionOptions? options = null, string name = "shabana.db")
    {
        var path = dir.File(name);
        var opts = options ?? new ConnectionOptions { Synchronous = SynchronousMode.Normal };
        var runner = new MigrationRunner(path, MigrationSet.Embedded(), new BackupService(), dir.File("backups"), "test", options: opts);
        runner.Run();
        return new TestDatabase(path, new ConnectionFactory(path, opts));
    }

    /// <summary>Creates (or upgrades) <paramref name="path"/> with a custom chain and backup service.</summary>
    public static TestDatabase CreateWith(string path, IReadOnlyList<Migration> chain, IBackupService backups, string backupRoot, ConnectionOptions? options = null)
    {
        var opts = options ?? new ConnectionOptions { Synchronous = SynchronousMode.Normal };
        new MigrationRunner(path, chain, backups, backupRoot, "test", options: opts).Run();
        return new TestDatabase(path, new ConnectionFactory(path, opts));
    }

    public static TestDatabase Open(string path, ConnectionOptions? options = null) =>
        new(path, new ConnectionFactory(path, options ?? new ConnectionOptions { Synchronous = SynchronousMode.Normal }));

    public T Scalar<T>(string sql)
    {
        using var connection = Factory.OpenExisting();
        using var cmd = connection.CreateCommand();
        cmd.CommandText = sql;
        return (T)System.Convert.ChangeType(cmd.ExecuteScalar()!, typeof(T), System.Globalization.CultureInfo.InvariantCulture);
    }

    public void Exec(string sql)
    {
        using var connection = Factory.OpenExisting();
        using var cmd = connection.CreateCommand();
        cmd.CommandText = sql;
        cmd.ExecuteNonQuery();
    }
}
