using MediaEngine.Identity.Contracts;

namespace MediaEngine.Admin.Tests;

public sealed class ResetTwoStepCommandTests
{
    [Fact]
    public async Task UnauthorizedHost_CannotChangeAnything()
    {
        var recovery = new Recovery(true);
        var console = new Console([]);

        var exitCode = await new ResetTwoStepCommand(new Authorizer(false), recovery, console).ExecuteAsync("owner@example.com");

        Assert.Equal(3, exitCode);
        Assert.Null(recovery.Email);
        Assert.Contains(console.Errors, message => message.Contains("elevated", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task AuthorizedHost_TurnsTwoStepOffForTheEmail()
    {
        var recovery = new Recovery(true);
        var console = new Console([]);

        var exitCode = await new ResetTwoStepCommand(new Authorizer(true), recovery, console).ExecuteAsync("owner@example.com");

        Assert.Equal(0, exitCode);
        Assert.Equal("owner@example.com", recovery.Email);
        Assert.Contains(console.Output, message => message.Contains("now off", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task AccountWithoutTwoStep_IsToldNothingChanged()
    {
        var console = new Console([]);

        var exitCode = await new ResetTwoStepCommand(new Authorizer(true), new Recovery(false), console).ExecuteAsync("owner@example.com");

        Assert.Equal(0, exitCode);
        Assert.Contains(console.Output, message => message.Contains("No changes", StringComparison.Ordinal));
    }

    [Fact]
    public async Task MissingEmail_IsAskedFor_AndBlankIsRefused()
    {
        var recovery = new Recovery(true);

        var asked = await new ResetTwoStepCommand(new Authorizer(true), recovery, new Console(["typed@example.com"])).ExecuteAsync(null);
        var blank = await new ResetTwoStepCommand(new Authorizer(true), new Recovery(true), new Console([""])).ExecuteAsync(null);

        Assert.Equal(0, asked);
        Assert.Equal("typed@example.com", recovery.Email);
        Assert.Equal(2, blank);
    }

    [Fact]
    public async Task UnknownAccount_ReportsTheErrorWithoutCrashing()
    {
        var console = new Console([]);

        var exitCode = await new ResetTwoStepCommand(new Authorizer(true), new Recovery(true, unknown: true), console).ExecuteAsync("nobody@example.com");

        Assert.Equal(3, exitCode);
        Assert.NotEmpty(console.Errors);
    }

    private sealed class Authorizer(bool allowed) : IHostRecoveryAuthorizer
    {
        public void EnsureAuthorized()
        {
            if (!allowed)
            {
                throw new UnauthorizedAccessException("This command needs elevated access on the host computer.");
            }
        }
    }

    private sealed class Recovery(bool hadTwoStep, bool unknown = false) : IHostTwoStepRecoveryService
    {
        public string? Email { get; private set; }

        public Task<bool> ResetTwoStepFromHostAsync(string email, CancellationToken ct = default)
        {
            if (unknown)
            {
                throw new UnauthorizedAccessException("No account has that email address.");
            }

            Email = email;
            return Task.FromResult(hadTwoStep);
        }
    }

    private sealed class Console(IEnumerable<string?> lines) : IAdminConsole
    {
        private readonly Queue<string?> _lines = new(lines);
        public List<string> Output { get; } = [];
        public List<string> Errors { get; } = [];
        public string? ReadLine(string prompt) => _lines.Dequeue();
        public string ReadSecret(string prompt) => throw new InvalidOperationException("No secrets are read.");
        public void WriteLine(string message) => Output.Add(message);
        public void WriteError(string message) => Errors.Add(message);
    }
}
