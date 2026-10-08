using System.Text.Json;
using Dapper;
using MediaEngine.Domain.Entities;
using MediaEngine.Domain.Enums;
using MediaEngine.Intelligence.Services;
using MediaEngine.Providers.Services;
using MediaEngine.Storage;

namespace MediaEngine.Providers.Tests;

public sealed class RetailMatrixPersistenceTests
{
    [Fact]
    public async Task FullMatrixBreakdownRoundTripsThroughTheCurrentCandidateTable()
    {
        var path = Path.Combine(Path.GetTempPath(), $"retail-matrix-{Guid.NewGuid():N}.db");
        DapperConfiguration.Configure();
        using var db = new DatabaseConnection(path);
        try
        {
            db.InitializeSchema();
            var job = Guid.NewGuid();
            using (var conn = db.CreateConnection())
            {
                conn.Execute("INSERT INTO identity_jobs(id,entity_id,entity_type,media_type) VALUES(@job,@entity,'MediaAsset','Books')", new { job, entity = Guid.NewGuid() });
            }
            var root = new DirectoryInfo(AppContext.BaseDirectory);
            while (root is not null && !File.Exists(Path.Combine(root.FullName, "config", "pipelines.json")))
            {
                root = root.Parent;
            }
            var scoring = new RetailMatchScoringService(new FuzzyMatchingService(), new ConfigurationDirectoryLoader(Path.Combine(root!.FullName, "config")));
            var hints = new Dictionary<string, string> { ["title"] = "Example", ["author"] = "Creator", ["year"] = "2020", ["language"] = "en", ["publisher"] = "Press" };
            var score = scoring.ScoreCandidate(hints, "Example", "Creator", "2020", MediaType.Books,
                extendedMetadata: new() { Kind = MediaType.Books, Language = "de", Publisher = "Press" });
            var decider = new RetailCandidateScorer();
            var decision = decider.EvaluateDecision(hints, "Example", "Creator", "2020", score, score.CompositeScore, .90, .65, "matrix-persistence", mediaType: MediaType.Books);
            var json = decider.BuildScoreBreakdownJson(score, decision, "matrix-persistence");
            var candidate = new RetailMatchCandidate { JobId = job, ProviderId = Guid.NewGuid(), ProviderName = "Offline fixture", Title = "Example", Creator = "Creator", Year = "2020", ScoreTotal = decision.FinalScore, Outcome = decision.Outcome, ScoreBreakdownJson = json };
            var repository = new RetailCandidateRepository(db);
            await repository.InsertBatchAsync([candidate]);
            var stored = Assert.Single(await repository.GetByJobAsync(job));
            Assert.Equal(json, stored.ScoreBreakdownJson);
            using var document = JsonDocument.Parse(stored.ScoreBreakdownJson!);
            var rows = document.RootElement.GetProperty("field_scores").EnumerateArray().ToList();
            Assert.Equal(score.FieldScores.Count, rows.Count);
            foreach (var role in new[] { "weighted", "gate", "bonus", "penalty" })
            {
                Assert.Contains(rows, row => row.GetProperty("Role").GetString() == role);
            }
            Assert.Equal("Creator", rows.Single(row => row.GetProperty("Key").GetString() == "author").GetProperty("CandidateValue").GetString());
            Assert.Equal(score.CompositeScore, document.RootElement.GetProperty("final_score").GetDouble());
        }
        finally
        {
            using (var conn = db.CreateConnection())
            {
                Microsoft.Data.Sqlite.SqliteConnection.ClearPool(conn);
            }
            db.Dispose(); File.Delete(path);
        }
    }
}
