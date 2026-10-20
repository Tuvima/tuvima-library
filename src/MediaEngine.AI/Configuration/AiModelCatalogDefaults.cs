namespace MediaEngine.AI.Configuration;

/// <summary>Supported launch catalogue only. Every entry has a wired local runtime.</summary>
public static class AiModelCatalogDefaults
{
    private const string AudioFile = "ggml-medium.bin";
    private const string AudioDownloadUrl = "https://huggingface.co/ggerganov/whisper.cpp/resolve/main/ggml-medium.bin";

    public static Dictionary<string, AiModelCatalogEntry> CreateCatalog() =>
        new(StringComparer.OrdinalIgnoreCase)
        {
            [AiResourceProfileCatalog.EssentialCatalogKey] = Text(
                "Qwen3 0.6B Q8", "essential", "Qwen3-0.6B-Q8_0.gguf",
                "https://huggingface.co/Qwen/Qwen3-0.6B-GGUF/resolve/main/Qwen3-0.6B-Q8_0.gguf",
                "https://huggingface.co/Qwen/Qwen3-0.6B-GGUF",
                "9465e63a22add5354d9bb4b99e90117043c7124007664907259bd16d043bb031",
                639, 1024, "Q8_0", 4096),
            [AiResourceProfileCatalog.StandardCatalogKey] = Text(
                "Qwen3 1.7B Q5_K_M", "standard", "Qwen3-1.7B-Q5_K_M.gguf",
                "https://huggingface.co/unsloth/Qwen3-1.7B-GGUF/resolve/main/Qwen3-1.7B-Q5_K_M.gguf",
                "https://huggingface.co/unsloth/Qwen3-1.7B-GGUF",
                "b0949de5b2e06cbed6aa96517f9bd8afb334584b6f95ee83479292ff4bdd8ed3",
                1260, 2048, "Q5_K_M", 8192),
            [AiResourceProfileCatalog.AdvancedCatalogKey] = Text(
                "Qwen3 4B Q4_K_M", "advanced", "Qwen3-4B-Q4_K_M.gguf",
                "https://huggingface.co/Qwen/Qwen3-4B-GGUF/resolve/main/Qwen3-4B-Q4_K_M.gguf",
                "https://huggingface.co/Qwen/Qwen3-4B-GGUF",
                "7485fe6f11af29433bc51cab58009521f205840f5b4ae3a32fa7f92e8534fdf5",
                2500, 4096, "Q4_K_M", 16384),
            [AiResourceProfileCatalog.AudioCatalogKey] = new AiModelCatalogEntry
            {
                DisplayName = "Whisper Medium",
                Family = "Whisper",
                Provider = "OpenAI / whisper.cpp",
                License = "MIT",
                Runtime = "Whisper.net/whisper.cpp",
                Status = "supported",
                SelectionTier = "optional-audio-pack",
                IntendedRoles = ["audio"],
                File = AudioFile,
                DownloadUrl = AudioDownloadUrl,
                SourceUrl = "https://huggingface.co/ggerganov/whisper.cpp",
                Sha256 = "6c14d5adee5f86394037b4e4e8b59f1673b6cee10e3cf0b11bbdbee79c156208",
                SizeMB = 1500,
                MemoryEnvelopeMB = 2048,
                Quantization = "F16",
                Capabilities = new AiModelCapabilities
                {
                    AudioInput = true,
                    TextOutput = true,
                    TimestampSegments = true,
                    SyncGrade = true,
                    Multilingual = true,
                },
                Compatibility = ReadyCompatibility("Whisper.net"),
                Readiness = DefinitionReadiness(AudioFile, AudioDownloadUrl),
                Validation = new AiModelValidationProfile
                {
                    BenchmarkSuite = "audio_sync",
                    MinTaskPassRate = 0.95,
                    MaxWer = 0.12,
                    MaxTimestampDriftMs = 250,
                },
                SelectionRationale = "The single supported optional audio feature-pack artifact.",
            },
        };

    public static Dictionary<string, AiRoleRequirement> CreateRoleRequirements() =>
        new(StringComparer.OrdinalIgnoreCase)
        {
            ["text_fast"] = Requirement("Interactive structured text", "text_instant"),
            ["text_quality"] = Requirement("Ingestion and scheduled text", "text_ingestion"),
            ["text_scholar"] = Requirement("Description intelligence", "text_enrichment"),
            ["text_cjk"] = Requirement("Multilingual text", "text_multilingual"),
            ["audio"] = new AiRoleRequirement
            {
                Description = "Optional transcription and alignment",
                RequiredCapabilities = ["audio_input", "text_output", "timestamp_segments"],
                PreferredCatalogKeys = [AiResourceProfileCatalog.AudioCatalogKey],
                BenchmarkSuite = "audio_sync",
                MaxDefaultSizeMB = 1600,
                MemoryEnvelopeMB = 2048,
            },
        };

    private static AiRoleRequirement Requirement(string description, string suite) => new()
    {
        Description = description,
        RequiredCapabilities = ["text_input", "text_output", "structured_json"],
        PreferredCatalogKeys =
        [
            AiResourceProfileCatalog.EssentialCatalogKey,
            AiResourceProfileCatalog.StandardCatalogKey,
            AiResourceProfileCatalog.AdvancedCatalogKey,
        ],
        BenchmarkSuite = suite,
        MaxDefaultSizeMB = 2500,
        MemoryEnvelopeMB = 4096,
        MaxContextLength = 16384,
    };

    private static AiModelCatalogEntry Text(
        string name, string tier, string file, string downloadUrl, string sourceUrl,
        string checksum, int sizeMb, int memoryMb, string quantization, int contextLength) => new()
        {
            DisplayName = name,
            Family = "Qwen3",
            Provider = "Qwen",
            License = "Apache-2.0",
            Runtime = "LLamaSharp/GGUF",
            Status = "supported",
            SelectionTier = tier,
            IntendedRoles = ["text"],
            File = file,
            DownloadUrl = downloadUrl,
            SourceUrl = sourceUrl,
            Sha256 = checksum,
            SizeMB = sizeMb,
            MemoryEnvelopeMB = memoryMb,
            Quantization = quantization,
            ContextLength = contextLength,
            MaxContextLength = contextLength,
            Capabilities = new AiModelCapabilities
            {
                TextInput = true,
                TextOutput = true,
                StructuredJson = true,
                Gbnf = true,
                Multilingual = true,
                Cjk = true,
            },
            Compatibility = ReadyCompatibility("LLamaSharp"),
            Readiness = DefinitionReadiness(file, downloadUrl),
            Validation = new AiModelValidationProfile
            {
                BenchmarkSuite = tier == "advanced" ? "text_enrichment" : "text_ingestion",
                MinJsonValidityRate = 0.99,
                MinTaskPassRate = 0.92,
                MaxHallucinationRate = 0.02,
            },
            SelectionRationale = $"Supported {tier} resource profile.",
            IntegrationNotes = "Executed through the single-resident LLamaSharp runtime.",
        };

    private static AiModelCompatibility ReadyCompatibility(string backend) => new()
    {
        SupportedBackends = [backend],
        MinimumRuntimeVersion = "bundled",
    };

    /// <summary>
    /// What the catalogue can know on its own: the definition is complete when it names a file and a download address.
    /// Whether the native runtime loads and whether the model file is on disk are facts about this machine; the
    /// <c>AiModelSelectionAdvisor</c> fills them in from the live checks, so they start false here.
    /// </summary>
    private static AiModelReadiness DefinitionReadiness(string file, string downloadUrl) => new()
    {
        ConfigurationReady = !string.IsNullOrWhiteSpace(file) && !string.IsNullOrWhiteSpace(downloadUrl),
        RuntimeReady = false,
        Validated = false,
    };
}
