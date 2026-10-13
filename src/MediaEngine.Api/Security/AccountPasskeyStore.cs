using System.Text.Json;
using Dapper;
using MediaEngine.Domain.Contracts;
using MediaEngine.Domain.Entities;
using MediaEngine.Storage.Contracts;
using Microsoft.AspNetCore.Identity;

namespace MediaEngine.Api.Security;

public sealed class AccountPasskeyStore(IAccountRepository accounts, IDatabaseConnection database) : IUserStore<Account>, IUserPasskeyStore<Account>
{
    public void Dispose() { }
    public Task<string> GetUserIdAsync(Account user, CancellationToken ct) => Task.FromResult(user.Id.ToString("D"));
    public Task<string?> GetUserNameAsync(Account user, CancellationToken ct) => Task.FromResult<string?>(user.Email ?? user.Id.ToString("D"));
    public Task SetUserNameAsync(Account user, string? userName, CancellationToken ct) => Task.CompletedTask;
    public Task<string?> GetNormalizedUserNameAsync(Account user, CancellationToken ct) => Task.FromResult(user.NormalizedEmail);
    // Tuvima owns normalized_email (it is null until an account has an email), so Identity's own normalising must not write the account id into it.
    public Task SetNormalizedUserNameAsync(Account user, string? normalizedName, CancellationToken ct) => Task.CompletedTask;
    public async Task<IdentityResult> CreateAsync(Account user, CancellationToken ct) { await accounts.InsertAsync(user, ct); return IdentityResult.Success; }
    public async Task<IdentityResult> UpdateAsync(Account user, CancellationToken ct) => await accounts.UpdateAsync(user, ct) ? IdentityResult.Success : IdentityResult.Failed(new IdentityError { Description = "Account was not found." });
    public Task<IdentityResult> DeleteAsync(Account user, CancellationToken ct) => Task.FromResult(IdentityResult.Failed(new IdentityError { Description = "Account deletion is managed by Tuvima." }));
    public async Task<Account?> FindByIdAsync(string userId, CancellationToken ct) => Guid.TryParse(userId, out var id) ? await accounts.GetByIdAsync(id, ct) : null;
    public Task<Account?> FindByNameAsync(string normalizedUserName, CancellationToken ct) => accounts.GetByNormalizedEmailAsync(normalizedUserName, ct);

    public Task AddOrUpdatePasskeyAsync(Account user, UserPasskeyInfo passkey, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested(); using var conn = database.CreateConnection();
        var json = JsonSerializer.Serialize(StoredPasskey.From(passkey));
        conn.Execute("""
            INSERT INTO account_passkeys(credential_id,account_id,name,data_json,created_at,last_used_at)
            VALUES(@credentialId,@accountId,@name,@json,@createdAt,NULL)
            ON CONFLICT(credential_id) DO UPDATE SET name=excluded.name,data_json=excluded.data_json,last_used_at=@lastUsedAt;
            """, new { credentialId = passkey.CredentialId, accountId = user.Id, name = passkey.Name ?? "Passkey", json, createdAt = passkey.CreatedAt.ToString("O"), lastUsedAt = DateTimeOffset.UtcNow.ToString("O") }); return Task.CompletedTask;
    }

    public Task<IList<UserPasskeyInfo>> GetPasskeysAsync(Account user, CancellationToken ct)
    { ct.ThrowIfCancellationRequested(); using var conn = database.CreateConnection(); var json = conn.Query<string>("SELECT data_json FROM account_passkeys WHERE account_id=@accountId ORDER BY created_at;", new { accountId = user.Id }); return Task.FromResult<IList<UserPasskeyInfo>>(json.Select(Deserialize).ToList()); }

    public async Task<Account?> FindByPasskeyIdAsync(byte[] credentialId, CancellationToken ct)
    { ct.ThrowIfCancellationRequested(); using var conn = database.CreateConnection(); var id = conn.QueryFirstOrDefault<Guid?>("SELECT account_id FROM account_passkeys WHERE credential_id=@credentialId LIMIT 1;", new { credentialId }); return id is null ? null : await accounts.GetByIdAsync(id.Value, ct); }

    public Task<UserPasskeyInfo?> FindPasskeyAsync(Account user, byte[] credentialId, CancellationToken ct)
    { ct.ThrowIfCancellationRequested(); using var conn = database.CreateConnection(); var json = conn.QueryFirstOrDefault<string>("SELECT data_json FROM account_passkeys WHERE account_id=@accountId AND credential_id=@credentialId LIMIT 1;", new { accountId = user.Id, credentialId }); return Task.FromResult(string.IsNullOrWhiteSpace(json) ? null : Deserialize(json)); }

    public Task RemovePasskeyAsync(Account user, byte[] credentialId, CancellationToken ct)
    { ct.ThrowIfCancellationRequested(); using var conn = database.CreateConnection(); conn.Execute("DELETE FROM account_passkeys WHERE account_id=@accountId AND credential_id=@credentialId;", new { accountId = user.Id, credentialId }); return Task.CompletedTask; }

    private static UserPasskeyInfo Deserialize(string json) => JsonSerializer.Deserialize<StoredPasskey>(json)?.ToIdentity() ?? throw new InvalidDataException("Stored passkey data is invalid.");
    private sealed record StoredPasskey(string CredentialId, string PublicKey, DateTimeOffset CreatedAt, uint SignCount, string[] Transports, bool IsUserVerified, bool IsBackupEligible, bool IsBackedUp, string AttestationObject, string ClientDataJson, string? Name)
    {
        public static StoredPasskey From(UserPasskeyInfo p) => new(Convert.ToBase64String(p.CredentialId), Convert.ToBase64String(p.PublicKey), p.CreatedAt, p.SignCount, p.Transports ?? [], p.IsUserVerified, p.IsBackupEligible, p.IsBackedUp, Convert.ToBase64String(p.AttestationObject), Convert.ToBase64String(p.ClientDataJson), p.Name);
        public UserPasskeyInfo ToIdentity() => new(Convert.FromBase64String(CredentialId), Convert.FromBase64String(PublicKey), CreatedAt, SignCount, Transports, IsUserVerified, IsBackupEligible, IsBackedUp, Convert.FromBase64String(AttestationObject), Convert.FromBase64String(ClientDataJson)) { Name = Name };
    }
}
