using System.Text.RegularExpressions;
using MediaEngine.Storage;
using MediaEngine.TestSupport;

namespace MediaEngine.Storage.Tests;

public sealed class SetupCodeRepositoryTests : IDisposable
{
    private static readonly DateTimeOffset Now = new(2026, 10, 9, 12, 0, 0, TimeSpan.Zero);
    private readonly string _databasePath = Path.Combine(
        Path.GetTempPath(), $"tuvima-setup-codes-{Guid.NewGuid():N}.db");
    private readonly DatabaseConnection _database;
    private readonly SetupCodeRepository _codes;

    public SetupCodeRepositoryTests()
    {
        _database = new DatabaseConnection(_databasePath);
        _database.InitializeSchema();
        _database.RunStartupChecks();
        _codes = new SetupCodeRepository(_database);
    }

    [Fact]
    public void GeneratedCodes_AreEightCharactersWithoutLookAlikes()
    {
        for (var i = 0; i < 200; i++)
        {
            var code = SetupCodeRepository.GenerateCode();

            Assert.Matches(new Regex("^[A-HJKMNP-Z2-9]{4}-[A-HJKMNP-Z2-9]{4}$"), code);
        }
    }

    [Fact]
    public async Task ValidCode_IsAcceptedOnceThenRefused()
    {
        await _codes.IssueAsync("ABCD-EFGH", Now, CancellationToken.None);

        Assert.Equal(SetupCodeCheck.Accepted, await _codes.VerifyAndConsumeAsync("abcd efgh", Now.AddMinutes(1), CancellationToken.None));
        Assert.Equal(SetupCodeCheck.NoActiveCode, await _codes.VerifyAndConsumeAsync("ABCD-EFGH", Now.AddMinutes(2), CancellationToken.None));
    }

    [Fact]
    public async Task ExpiredCode_IsRefused()
    {
        await _codes.IssueAsync("ABCD-EFGH", Now, CancellationToken.None);

        var afterExpiry = Now + SetupCodeRepository.Lifetime + TimeSpan.FromSeconds(1);

        Assert.Equal(SetupCodeCheck.NoActiveCode, await _codes.VerifyAndConsumeAsync("ABCD-EFGH", afterExpiry, CancellationToken.None));
    }

    [Fact]
    public async Task CodeStillWorksJustBeforeItExpires()
    {
        await _codes.IssueAsync("ABCD-EFGH", Now, CancellationToken.None);

        var justBefore = Now + SetupCodeRepository.Lifetime - TimeSpan.FromSeconds(1);

        Assert.Equal(SetupCodeCheck.Accepted, await _codes.VerifyAndConsumeAsync("ABCD-EFGH", justBefore, CancellationToken.None));
    }

    [Fact]
    public async Task FiveWrongCodes_RetireTheCodeEvenIfTheRightOneFollows()
    {
        await _codes.IssueAsync("ABCD-EFGH", Now, CancellationToken.None);

        for (var i = 1; i < SetupCodeRepository.MaxFailedAttempts; i++)
        {
            Assert.Equal(SetupCodeCheck.Wrong, await _codes.VerifyAndConsumeAsync("ZZZZ-ZZZZ", Now, CancellationToken.None));
        }

        Assert.Equal(SetupCodeCheck.TooManyAttempts, await _codes.VerifyAndConsumeAsync("ZZZZ-ZZZZ", Now, CancellationToken.None));
        Assert.Equal(SetupCodeCheck.NoActiveCode, await _codes.VerifyAndConsumeAsync("ABCD-EFGH", Now, CancellationToken.None));
    }

    [Fact]
    public async Task FourWrongCodes_DoNotStopTheRightOne()
    {
        await _codes.IssueAsync("ABCD-EFGH", Now, CancellationToken.None);

        for (var i = 1; i < SetupCodeRepository.MaxFailedAttempts; i++)
        {
            await _codes.VerifyAndConsumeAsync("ZZZZ-ZZZZ", Now, CancellationToken.None);
        }

        Assert.Equal(SetupCodeCheck.Accepted, await _codes.VerifyAndConsumeAsync("ABCD-EFGH", Now, CancellationToken.None));
    }

    [Fact]
    public async Task NewCode_InvalidatesTheOlderOne()
    {
        await _codes.IssueAsync("AAAA-AAAA", Now, CancellationToken.None);
        await _codes.IssueAsync("BBBB-BBBB", Now.AddMinutes(1), CancellationToken.None);

        Assert.Equal(SetupCodeCheck.Wrong, await _codes.VerifyAndConsumeAsync("AAAA-AAAA", Now.AddMinutes(2), CancellationToken.None));
        Assert.Equal(SetupCodeCheck.Accepted, await _codes.VerifyAndConsumeAsync("BBBB-BBBB", Now.AddMinutes(2), CancellationToken.None));
    }

    [Fact]
    public async Task OnlyAHashIsStored()
    {
        await _codes.IssueAsync("ABCD-EFGH", Now, CancellationToken.None);

        var stored = _codes.NewestStoredHash();

        Assert.NotNull(stored);
        Assert.DoesNotContain("ABCD", stored, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(64, stored!.Length);
        using var connection = _database.CreateConnection();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT typeof(id) FROM setup_codes LIMIT 1;";
        Assert.Equal("blob", command.ExecuteScalar());
    }

    public void Dispose()
    {
        _database.Dispose();
        TestTemp.DeleteDatabase(_databasePath);
    }
}
