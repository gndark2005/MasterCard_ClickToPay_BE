using MC_ClickToPay.Services.Database;
using Xunit;

namespace MC_ClickToPay.Services.Tests;

public sealed class DatabaseMigratorTests
{
    [Fact]
    public void MigrationsShipInsideTheServicesAssembly()
    {
        var first = DatabaseMigrator.Embedded[0];

        Assert.Equal(1, first.Version);
        Assert.Equal("create_checkout_transactions", first.Name);
        Assert.Contains("CREATE TABLE IF NOT EXISTS checkout_transactions", first.Sql, StringComparison.Ordinal);
    }

    [Fact]
    public void MigrationsAreInVersionOrderWithoutGapsOrDuplicates()
    {
        var versions = DatabaseMigrator.Embedded.Select(m => m.Version).ToList();

        Assert.Equal(Enumerable.Range(1, versions.Count), versions);
    }

    [Fact]
    public void ChecksumIgnoresLineEndingsButNotContent()
    {
        var lf = new DatabaseMigration(1, "x", "CREATE TABLE t (id INT);\nSELECT 1;\n");
        var crlf = new DatabaseMigration(1, "x", "CREATE TABLE t (id INT);\r\nSELECT 1;\r\n");
        var edited = new DatabaseMigration(1, "x", "CREATE TABLE t (id BIGINT);\nSELECT 1;\n");

        Assert.Equal(lf.Checksum, crlf.Checksum);
        Assert.NotEqual(lf.Checksum, edited.Checksum);
        Assert.Equal(64, lf.Checksum.Length);
    }

    /// <summary>
    /// Applied migrations must never be edited: databases already store these checksums and the API would refuse to
    /// start. Add a new migration (next version) instead, and add its checksum here once it has been deployed.
    /// </summary>
    [Fact]
    public void DeployedMigrationsAreNotEdited()
    {
        Dictionary<int, string> deployed = new()
        {
            [1] = "57d3b3c24d5daa55ccbb3b3b2a7ec3d02d4e6191b7aa40e0ddc3a997257773f5",
        };

        foreach (var (version, checksum) in deployed)
        {
            Assert.Equal(checksum, DatabaseMigrator.Embedded.Single(m => m.Version == version).Checksum);
        }
    }
}
