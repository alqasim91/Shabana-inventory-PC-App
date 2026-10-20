using Shabana.Data;
using Shabana.Data.Maintenance;

namespace Shabana.Tests.Data;

public class IntegrityCheckerTests(PopulatedV1Fixture fixture) : IClassFixture<PopulatedV1Fixture>
{
    [Fact]
    public void A_healthy_database_passes_the_quick_check_and_the_full_check_of_a_busy_one_is_clean()
    {
        using var dir = new TempDir();
        var path = fixture.CopyTo(dir);
        IntegrityChecker.QuickCheck(path).ShouldBeEmpty();
        using var c = new Shabana.Data.Sqlite.ConnectionFactory(path).OpenForInspection();
        var report = IntegrityChecker.Full(c);
        report.IsClean.ShouldBeTrue();
        report.Invariants.Count.ShouldBe(14);
    }

    [Fact]
    public void Damage_and_non_databases_are_reported_as_problems_not_exceptions()
    {
        using var dir = new TempDir();
        var path = fixture.CopyTo(dir);
        var bytes = File.ReadAllBytes(path);
        for (var i = bytes.Length / 4; i < (bytes.Length / 4) + 12288; i++)
        {
            bytes[i] = 0xAB;
        }

        File.WriteAllBytes(path, bytes);
        IntegrityChecker.QuickCheck(path).ShouldNotBeEmpty();

        var junk = dir.File("junk.db");
        File.WriteAllBytes(junk, Enumerable.Range(0, 9000).Select(i => (byte)(i % 251)).ToArray());
        IntegrityChecker.QuickCheck(junk).ShouldNotBeEmpty();
    }

    [Fact]
    public void Data_locations_follow_the_documented_layout()
    {
        var l = new DataLocations(Path.Combine(Path.GetTempPath(), "ShabanaInventory"));
        l.DatabasePath.ShouldEndWith(Path.Combine("ShabanaInventory", "db", "shabana.db"));
        l.DefaultBackupRoot.ShouldEndWith(Path.Combine("ShabanaInventory", "Backups"));
        l.RestoreFolder.ShouldEndWith(Path.Combine("ShabanaInventory", "restore"));
        DataLocations.Default().Root.ShouldEndWith("ShabanaInventory");
    }
}
