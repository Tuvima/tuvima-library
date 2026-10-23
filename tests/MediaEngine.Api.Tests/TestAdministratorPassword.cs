using MediaEngine.Domain.Contracts;
using MediaEngine.Domain.Entities;

namespace MediaEngine.Api.Tests;

/// <summary>
/// Test shorthand: a Library with several households needs every server administrator to have an email and a password first
/// (users, profiles and households D10), so a test scene that adds a second household gives its server administrator one.
/// The hash is a stand-in; nothing here signs in with it.
/// </summary>
internal static class TestAdministratorPassword
{
    public static Task GiveAsync(this IIdentityRepository identities, Guid accountId) =>
        identities.UpsertAccountCredentialAsync(new AccountCredential
        {
            Id = Guid.NewGuid(),
            AccountId = accountId,
            SecretHash = "work-factored-hash",
            SecurityStamp = "test-security-stamp",
            CreatedAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow,
        });
}
