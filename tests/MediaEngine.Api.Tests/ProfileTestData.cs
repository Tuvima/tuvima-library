using Dapper;
using MediaEngine.Domain.Aggregates;
using MediaEngine.Storage.Contracts;

namespace MediaEngine.Api.Tests;

internal static class ProfileTestData
{
    public static readonly Guid TestHouseholdId = new("7e57a000-0000-0000-0000-000000000001");

    public static Task InsertAsync(IDatabaseConnection database, Profile profile, Guid? householdId = null)
    {
        using var connection = database.CreateConnection();
        // Test people share one household, like a real household; Shared Library scope follows households.
        var household = householdId ?? TestHouseholdId;
        connection.Execute(
            "INSERT OR IGNORE INTO households(id,name,created_at) VALUES(@TestHouseholdId,'Test household',@CreatedAt);",
            new { TestHouseholdId = household, CreatedAt = profile.CreatedAt.ToString("O") });
        connection.Execute("""
            INSERT INTO profiles(id,display_name,avatar_color,avatar_image_path,role,created_at,navigation_config,household_id)
            VALUES(@Id,@DisplayName,@AvatarColor,@AvatarImagePath,@Role,@CreatedAt,@NavigationConfig,@TestHouseholdId);
            """, new
        {
            TestHouseholdId = household,
            profile.Id,
            profile.DisplayName,
            profile.AvatarColor,
            profile.AvatarImagePath,
            Role = profile.Role.ToString(),
            CreatedAt = profile.CreatedAt.ToString("O"),
            profile.NavigationConfig,
        });
        return Task.CompletedTask;
    }
}
