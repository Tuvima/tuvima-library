using MediaEngine.Contracts.Details;
using MediaEngine.Contracts.Editing;

namespace MediaEngine.Web.Tests.Support;

/// <summary>A supporting file of a course as the Engine's editor read gives it (universal editor v5 WP7); every test starts from this and changes what it needs.</summary>
internal static class SupportingFileEditorTestData
{
    public static readonly Guid TitleId = Guid.Parse("c1000000-0000-0000-0000-000000000001");
    public static readonly Guid FileId = Guid.Parse("c1000000-0000-0000-0000-0000000000f1");
    public static readonly Guid OtherFileId = Guid.Parse("c1000000-0000-0000-0000-0000000000f2");
    public const string TitleName = "Steph Curry Teaches Shooting";

    public static EditorHeaderConfigDto Config() => new()
    {
        Kind = "supporting_file",
        Tabs = ["details", "preview", "history"],
        FactsLine = ["file_type", "pages_or_length", "size", "supporting_file_chip"],
        QuickFacts = ["file_type", "file_name", "folder", "read_only", "preview"],
    };

    public static EditorSupportingFileDto File(
        Guid? id = null,
        string scopeKey = "root",
        string scopeSource = "folder",
        string? alongsideKey = null,
        string? inferredAlongsideKey = null,
        string? group = null,
        string? groupSource = null,
        bool readOnly = false,
        bool missing = false,
        string type = SupportingFileListDefaults.TypeDocument,
        string typeLabel = "PDF",
        string? ownTitle = null,
        string? description = null,
        IReadOnlyList<string>? tags = null)
    {
        var fileId = id ?? FileId;
        var title = ownTitle ?? "Workbook";
        return new EditorSupportingFileDto
        {
            TitleId = TitleId,
            File = new SupportingFileDto
            {
                Id = fileId.ToString("D"),
                Kind = type == SupportingFileListDefaults.TypeDocument ? DetailExtraCardKinds.Document : DetailExtraCardKinds.Clip,
                FileName = "Workbook.pdf",
                Title = title,
                OwnTitle = ownTitle,
                Description = description,
                Group = group,
                GroupSource = groupSource,
                Role = "workbook",
                RoleLabel = "Workbook",
                ScopeKey = scopeKey,
                InferredScopeKey = "season:2",
                ScopeSource = scopeSource,
                AlongsideKey = alongsideKey,
                InferredAlongsideKey = inferredAlongsideKey,
                Tags = tags ?? [],
                SizeBytes = 2_516_582,
                PageCount = 18,
                IsMissing = missing,
            },
            Type = type,
            TypeLabel = typeLabel,
            Badge = "PDF",
            MimeType = "application/pdf",
            Folder = "Season 02/Documents",
            ReadOnly = readOnly,
            Roles =
            [
                new EditorSupportingFileRoleDto { Id = "workbook", Label = "Workbook" },
                new EditorSupportingFileRoleDto { Id = "guide", Label = "Guide" },
                new EditorSupportingFileRoleDto { Id = "other", Label = "Other" },
            ],
            Groups = ["Documents", "Transcripts"],
            Places =
            [
                new EditorSupportingFilePlaceDto { Key = "root", Label = TitleName, Level = "root" },
                new EditorSupportingFilePlaceDto { Key = "season:1", Label = "Season 01", Level = "season", ParentKey = "root" },
                new EditorSupportingFilePlaceDto { Key = "season:2", Label = "Season 02", Level = "season", ParentKey = "root" },
                new EditorSupportingFilePlaceDto { Key = "episode:s2e1", Label = "S02E01 Warm-up", Level = "episode", ParentKey = "season:2" },
                new EditorSupportingFilePlaceDto { Key = "episode:s2e3", Label = "S02E03 Footwork", Level = "episode", ParentKey = "season:2" },
                new EditorSupportingFilePlaceDto { Key = "episode:s1e1", Label = "S01E01 Welcome", Level = "episode", ParentKey = "season:1" },
            ],
            ScopeLabel = scopeKey == "root" ? TitleName : "Season 02",
            FolderScopeLabel = "Season 02 / Documents",
            Header = new EditorHeaderDto
            {
                Kind = "video",
                Root = new EditorHeaderRootDto { Id = TitleId, Title = TitleName, MediaType = "Videos" },
                CurrentId = TitleId,
                Label = "Course",
                LabelIsKind = true,
                Level = new EditorHeaderLevelDto
                {
                    SupportingFiles = new EditorHeaderSupportingFilesDto
                    {
                        Count = 2,
                        TotalInRoot = 2,
                        Files =
                        [
                            new EditorHeaderSupportingFileDto { Id = fileId, Title = title, TypeBadge = "PDF", Kind = "document", Role = "workbook", RoleLabel = "Workbook", IsCurrent = true },
                            new EditorHeaderSupportingFileDto { Id = OtherFileId, Title = "Transcript", TypeBadge = "PDF", Kind = "document", Role = "transcript", RoleLabel = "Transcript" },
                        ],
                    },
                },
                Config = EditorHeaderTestData.Config("video", "details", "artwork", "history"),
            },
            Config = Config(),
        };
    }
}
