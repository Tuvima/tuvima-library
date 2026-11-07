using MediaEngine.Api.Services.Extras;
using MediaEngine.Contracts.Details;
using MediaEngine.Domain.Configuration;
using MediaEngine.Domain.Models;

namespace MediaEngine.Api.Tests;

/// <summary>What a supporting file reads as on the page's Extras row and in the editor's list (universal editor v5 WP2).</summary>
public sealed class SupportingFileReadTests
{
    private static ItemExtra Worksheet() => new(
        Guid.NewGuid(), "lib", "/library/Course", "/library/Course/Section 1/02-exercises.pdf", ItemExtraKinds.Document, ItemExtraOrigins.Rule,
        "02-exercises", null, 0, 2048, null, 3, DateTimeOffset.UtcNow,
        InferredRole: ExtraRoles.Worksheet,
        GroupName: "Homework",
        GroupSource: ExtraGroupSources.You,
        InferredScope: ExtraScope.ForFolder("Section 1"),
        InferredAlongside: ExtraScope.ForFile("Section 1/02 Components.mkv"),
        Tags: ["maths"]);

    [Fact]
    public void ACard_CarriesTheTitleRoleGroupScopeAndTagsTheEditorSets()
    {
        var extra = Worksheet() with { OwnTitle = "Components exercises", OwnDescription = "Do these after lesson 2" };

        var card = Assert.Single(ExtraCardBuilder.Build([extra], _ => true));

        Assert.Equal("Components exercises", card.Title);
        Assert.Equal("Do these after lesson 2", card.Description);
        Assert.Equal(ExtraRoles.Worksheet, card.Role);
        Assert.Equal("Worksheet", card.RoleLabel);
        Assert.Equal("Homework", card.Group);
        Assert.Equal("folder:Section 1", card.ScopeKey);
        Assert.Equal(ExtraScopeSources.Folder, card.ScopeSource);
        Assert.Equal("file:Section 1/02 Components.mkv", card.AlongsideKey);
        Assert.Equal(["maths"], card.Tags);
        Assert.Equal("Section 1", card.Place);
    }

    [Fact]
    public void ASupportingFileRead_KeepsWhatAPersonSetBesideWhatTheFolderSaid()
    {
        var extra = Worksheet() with { Scope = ExtraScope.Root, Role = ExtraRoles.Guide };

        var dto = ExtraCardBuilder.ToSupportingFile(extra, _ => true);

        Assert.Equal("root", dto.ScopeKey);
        Assert.Equal("folder:Section 1", dto.InferredScopeKey);
        Assert.Equal(ExtraScopeSources.You, dto.ScopeSource);
        Assert.Equal(ExtraRoles.Guide, dto.Role);
        Assert.True(dto.RoleIsSetByYou);
        Assert.Equal("02-exercises.pdf", dto.FileName);
        Assert.Equal(DetailExtraCardKinds.Document, dto.Kind);
        Assert.Equal("file:Section 1/02 Components.mkv", dto.InferredAlongsideKey);
    }

    [Fact]
    public void TheRoleLabel_IsTheWordsInTheConfiguration_WhenTheRulesAreGiven()
    {
        var rules = new ExtrasConfiguration
        {
            Roles = new Dictionary<string, List<ExtrasRoleEntry>>(StringComparer.OrdinalIgnoreCase)
            {
                ["document"] = [new() { Id = ExtraRoles.Worksheet, Label = "Homework sheet" }],
            },
        };

        Assert.Equal("Homework sheet", Assert.Single(ExtraCardBuilder.Build([Worksheet()], _ => true, rules)).RoleLabel);
        Assert.Equal("Homework sheet", ExtraCardBuilder.ToSupportingFile(Worksheet(), _ => true, rules).RoleLabel);
        Assert.Equal("Worksheet", ExtraCardBuilder.ToSupportingFile(Worksheet(), _ => true).RoleLabel);
    }

    [Fact]
    public void AFileThatLeftTheDisk_ReadsAsMissing()
    {
        var extra = Worksheet() with { MissingSince = DateTimeOffset.UtcNow };

        Assert.True(ExtraCardBuilder.ToSupportingFile(extra, _ => true).IsMissing);
        Assert.True(ExtraCardBuilder.ToSupportingFile(Worksheet(), _ => false).IsMissing);
    }
}
