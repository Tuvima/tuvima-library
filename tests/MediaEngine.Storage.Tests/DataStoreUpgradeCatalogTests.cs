using MediaEngine.Storage.Schema.Upgrades;
using MediaEngine.Storage.Tests.UpgradeCodeSteps;

namespace MediaEngine.Storage.Tests
{
    public sealed class DataStoreUpgradeCatalogTests
    {
        private const string ValidPrefix = "MediaEngine.Storage.Tests.UpgradeSteps.Valid.";
        private const string CodeStepNamespace = "MediaEngine.Storage.Tests.UpgradeCodeSteps";

        private static SqlDataStoreUpgradeStep Sql(int number, string name, string script = "SELECT 1;")
            => new(number, name, script, $"test:{number}_{name}");

        [Fact]
        public void EmptyProductCatalog_IsValid()
        {
            var assembly = typeof(DataStoreUpgradeCatalogTests).Assembly;

            var loaded = DataStoreUpgradeCatalog.Load(assembly, "MediaEngine.Storage.Tests.UpgradeSteps.Nothing.", "MediaEngine.Storage.Tests.NoCodeSteps");
            var validated = DataStoreUpgradeCatalog.Validate([]);

            Assert.Empty(loaded);
            Assert.Empty(validated);
        }

        [Fact]
        public void SqlResourcesAndCodeSteps_LoadIntoOneNumbering()
        {
            var assembly = typeof(DataStoreUpgradeCatalogTests).Assembly;

            var steps = DataStoreUpgradeCatalog.Load(assembly, ValidPrefix, CodeStepNamespace);

            Assert.Equal([1, 2, 3], steps.Select(step => step.Number));
            Assert.Equal(["create_things", "add_note_column", "seed_thing"], steps.Select(step => step.Name));
            Assert.IsType<SqlDataStoreUpgradeStep>(steps[0]);
            Assert.IsType<SeedThingCodeStep>(steps[2]);
        }

        [Fact]
        public void CatalogWithGapOrDuplicateNumbers_FailsValidation()
        {
            var gap = Assert.Throws<DataStoreUpgradeCatalogException>(
                () => DataStoreUpgradeCatalog.Validate([Sql(1, "first"), Sql(3, "third")]));
            var duplicate = Assert.Throws<DataStoreUpgradeCatalogException>(
                () => DataStoreUpgradeCatalog.Validate([Sql(1, "first"), Sql(1, "also_first")]));
            var notFromOne = Assert.Throws<DataStoreUpgradeCatalogException>(
                () => DataStoreUpgradeCatalog.Validate([Sql(2, "second")]));

            Assert.Contains("0002", gap.Message);
            Assert.Contains("0003", gap.Message);
            Assert.Contains("0001", duplicate.Message);
            Assert.Contains("also_first", duplicate.Message);
            Assert.Contains("0001", notFromOne.Message);
        }

        [Fact]
        public void CatalogWithGapBetweenSqlAndCodeSteps_FailsValidation()
        {
            Assert.Throws<DataStoreUpgradeCatalogException>(
                () => DataStoreUpgradeCatalog.Validate([Sql(1, "first"), new SeedThingCodeStep(), Sql(4, "fourth")]));
        }

        [Theory]
        [InlineData("Bad_Name")]
        [InlineData("double__underscore")]
        [InlineData("trailing_")]
        [InlineData("has-dash")]
        [InlineData("")]
        public void CatalogWithBadName_FailsValidation(string name)
        {
            var exception = Assert.Throws<DataStoreUpgradeCatalogException>(
                () => DataStoreUpgradeCatalog.Validate([Sql(1, name)]));

            Assert.Contains("test:1_", exception.Message);
        }

        [Fact]
        public void ResourceNotNamedLikeAStep_FailsLoadingAndNamesTheResource()
        {
            var assembly = typeof(DataStoreUpgradeCatalogTests).Assembly;

            var exception = Assert.Throws<DataStoreUpgradeCatalogException>(
                () => DataStoreUpgradeCatalog.Load(assembly, "MediaEngine.Storage.Tests.UpgradeSteps.BadName."));

            Assert.Contains("0001_Bad_Name.sql", exception.Message);
        }

        [Theory]
        [InlineData("PRAGMA foreign_keys = OFF;")]
        [InlineData("pragma  Foreign_Keys=on;")]
        [InlineData("PRAGMA journal_mode = DELETE;")]
        public void SqlStepWithForeignKeysPragma_FailsValidation(string statement)
        {
            var exception = Assert.Throws<DataStoreUpgradeCatalogException>(
                () => DataStoreUpgradeCatalog.Validate([Sql(1, "pragma_step", $"CREATE TABLE t (id INTEGER);\n{statement}\n")]));

            Assert.Contains("test:1_pragma_step", exception.Message);
        }

        [Fact]
        public void SqlStepWithForeignKeyCheck_PassesValidation()
        {
            var steps = DataStoreUpgradeCatalog.Validate([Sql(1, "check_step", "PRAGMA foreign_key_check;")]);

            Assert.Single(steps);
        }

        [Fact]
        public void CrlfAndLfScripts_HaveTheSameFingerprint()
        {
            var lf = Sql(1, "same", "CREATE TABLE t (\n    id INTEGER\n);\n");
            var crlf = Sql(1, "same", "CREATE TABLE t (\r\n    id INTEGER\r\n);\r\n");
            var edited = Sql(1, "same", "CREATE TABLE t (\n    id INTEGER,\n    x TEXT\n);\n");

            Assert.Equal(lf.Fingerprint, crlf.Fingerprint);
            Assert.NotEqual(lf.Fingerprint, edited.Fingerprint);
            Assert.Matches("^[0-9a-f]{64}$", lf.Fingerprint);
        }

        [Fact]
        public void ForeignKeysOffMarker_IsReadFromTheFirstLineOnly()
        {
            var declared = Sql(1, "off", "-- tuvima:foreign-keys-off\r\nSELECT 1;");
            var notFirstLine = Sql(2, "on", "SELECT 1;\n-- tuvima:foreign-keys-off\n");

            Assert.True(declared.RequiresForeignKeysOff);
            Assert.False(notFirstLine.RequiresForeignKeysOff);
        }

        [Fact]
        public void CodeStepFingerprint_DependsOnNumberNameAndType()
        {
            var first = new SeedThingCodeStep();
            var second = new SeedThingCodeStep();

            Assert.Equal(first.Fingerprint, second.Fingerprint);
            Assert.Matches("^[0-9a-f]{64}$", first.Fingerprint);
        }
    }
}

namespace MediaEngine.Storage.Tests.UpgradeCodeSteps
{
    /// <summary>Sample code step discovered by the catalog tests; inserts one row into <c>things</c>.</summary>
    internal sealed class SeedThingCodeStep : DataStoreCodeUpgradeStep
    {
        public override int Number => 3;

        public override string Name => "seed_thing";

        public override void Apply(Microsoft.Data.Sqlite.SqliteConnection connection, Microsoft.Data.Sqlite.SqliteTransaction transaction)
        {
            using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = "INSERT INTO things (id, label, note) VALUES (1, 'seeded', 'by code step');";
            command.ExecuteNonQuery();
        }
    }
}
