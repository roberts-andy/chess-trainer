using System.Net;
using System.Net.Http.Json;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using ChessTrainer.Core;
using ChessTrainer.Web;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace ChessTrainer.Tests;

public sealed class CloudTests : IAsyncLifetime
{
    private const string Owner = "11111111-1111-1111-1111-111111111111";
    private const string Tenant = "22222222-2222-2222-2222-222222222222";
    private const string Initial = "rnbqkbnr/pppppppp/8/8/8/8/PPPPPPPP/RNBQKBNR w KQkq - 0 1";
    private readonly string directory = Path.Combine(Path.GetTempPath(), "ChessTrainer.CloudTests", Guid.NewGuid().ToString("N"));
    private WebApplicationFactory<Program> factory = null!;
    private HttpClient client = null!;
    private CloudStore Store => factory.Services.GetRequiredService<CloudStore>();
    private static ClaimsPrincipal User(string owner = Owner, string tenant = Tenant) =>
        new(new ClaimsIdentity([new Claim("oid", owner), new Claim("tid", tenant)], "Test"));

    private WebApplicationFactory<Program> Factory(bool authenticated = true) => new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
    {
        builder.UseEnvironment("Development");
        builder.ConfigureAppConfiguration((_, configuration) => configuration.AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Storage:SqlitePath"] = Path.Combine(directory, "cloud.db"),
            ["Authentication:OwnerId"] = Owner,
            ["Authentication:TenantId"] = Tenant,
            ["Authentication:DevelopmentIdentity"] = authenticated.ToString()
        }));
    });

    public async Task InitializeAsync()
    {
        Directory.CreateDirectory(directory);
        factory = Factory();
        client = factory.CreateClient();
        client.DefaultRequestHeaders.Add("X-ChessTrainer-Request", "1");
        await Store.InitializeAsync();
        await Store.ImportPuzzlesAsync([new("sample", Initial, "e2e4 e7e5 g1f3", 1000, "opening", "")]);
    }

    [Fact]
    public async Task PuzzleCountsAndRatingSelectionFollowUpserts()
    {
        var first = new Puzzle("a", Initial, "e2e4 e7e5", 900, "", "");
        var second = first with { Id = "b", Rating = 1100 };
        await Store.ImportPuzzlesAsync([first, second]);
        Assert.Equal(3, await Store.PuzzleCountAsync());
        await Store.ImportPuzzlesAsync([first with { Rating = 1300 }, second, second with { Id = "B" }]);
        await Store.InitializeAsync();
        Assert.Equal(4, await Store.PuzzleCountAsync());
        Assert.Null(await Store.RandomPuzzleAsync(900, 900, null));
        Assert.Equal("B", (await Store.RandomPuzzleAsync(1100, 1100, "b"))!.Id);
        Assert.Equal("a", (await Store.RandomPuzzleAsync(1300, 1300, "a"))!.Id);
    }

    [Fact]
    public async Task ExistingLibrariesReceiveExactRatingCounts()
    {
        await using var connection = new Microsoft.Data.Sqlite.SqliteConnection(
            new Microsoft.Data.Sqlite.SqliteConnectionStringBuilder { DataSource = Path.Combine(directory, "cloud.db") }.ToString());
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = """
            DROP TRIGGER CloudPuzzleInserted;
            DROP TRIGGER CloudPuzzleUpdated;
            DROP TRIGGER CloudPuzzleDeleted;
            DROP TABLE CloudPuzzleRatings;
            """;
        await command.ExecuteNonQueryAsync();
        await Store.InitializeAsync();
        Assert.Equal(1, await Store.PuzzleCountAsync());
        Assert.Equal("sample", (await Store.RandomPuzzleAsync(1000, 1000, null))!.Id);
    }

    [Fact]
    public async Task ApiNeverDisclosesSolutionBeforeSubmitAndVisualizationStaysFrozen()
    {
        var response = await client.PostAsJsonAsync("/api/attempts", new StartRequest(900, 1100, Mode: SolveMode.Visualization));
        response.EnsureSuccessStatusCode();
        var view = (await response.Content.ReadFromJsonAsync<SessionView>(JsonOptions))!;
        Assert.Null(view.Solution);
        Assert.Null(view.Results);
        Assert.DoesNotContain("g1f3", await response.Content.ReadAsStringAsync());
        string start = view.Fen;
        view = await Command(view, "notation", "e5 Nf3");
        Assert.Equal(start, view.Fen);
        Assert.Null(view.Results);
        view = await Command(view, "submit");
        Assert.True(view.Submitted);
        Assert.Equal("e5 Nf3", view.Solution);
        Assert.Equal(Verdict.Correct, Assert.Single(view.Results!).Verdict);
        Assert.NotEqual(start, view.Fen);
        view = await Command(view, "annotation", "Reviewed on my phone");
        Assert.Equal("Reviewed on my phone", view.Lines[0].Annotations[2].Comment);
    }

    [Fact]
    public async Task StaleDeviceEditsConflictWithoutOverwritingSavedMoves()
    {
        using var scope = factory.Services.CreateScope();
        var training = scope.ServiceProvider.GetRequiredService<CloudTraining>();
        var initial = await training.StartAsync(User(), new());
        var edited = await training.ApplyAsync(User(), initial.Id, new("notation", 0, Value: "e5"));
        var response = await client.PostAsJsonAsync($"/api/attempts/{initial.Id}/commands", new SessionCommand("notation", 0, Value: "c5"));
        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        var saved = await training.ReadAsync(User(), edited.Id);
        Assert.Equal("e7e5", Assert.Single(saved.Lines[0].Moves));
    }

    [Fact]
    public async Task PositionEditsOverwriteAndOwnershipIsEnforced()
    {
        var study = new Study(Guid.NewGuid(), "Opening");
        await Store.SaveStudyAsync(Owner, study, true);
        var position = StudyPosition.Create(study.Id, "One", Initial);
        await Store.SavePositionsAsync(Owner, study.Id, [position], true);
        var edited = position with { Title = "Revised", Notes = "Only one position" };
        await Store.SavePositionsAsync(Owner, study.Id, [edited], false);
        await Store.SavePositionsAsync(Owner, study.Id, [edited], false);
        Assert.Equal(edited, Assert.Single(await Store.PositionsAsync(Owner, study.Id)));
        Assert.Empty(await Store.PositionsAsync("another-owner", study.Id));
        await Assert.ThrowsAsync<KeyNotFoundException>(() => Store.PositionAsync("another-owner", position.Id));
        await Assert.ThrowsAsync<KeyNotFoundException>(() => Store.SavePositionsAsync("another-owner", study.Id, [edited], false));
        var access = factory.Services.GetRequiredService<CloudAccess>();
        Assert.Throws<UnauthorizedAccessException>(() => access.Owner(User(tenant: Guid.NewGuid().ToString())));
        Assert.Throws<UnauthorizedAccessException>(() => access.Owner(User(owner: Guid.NewGuid().ToString())));
    }

    [Fact]
    public async Task ApiRejectsMissingAuthenticationForgedHeadersAndCrossSiteWrites()
    {
        using var anonymousFactory = Factory(false);
        using var anonymous = anonymousFactory.CreateClient();
        anonymous.DefaultRequestHeaders.Add("X-MS-CLIENT-PRINCIPAL", Convert.ToBase64String(Encoding.UTF8.GetBytes(
            $$"""{"claims":[{"typ":"oid","val":"{{Owner}}"},{"typ":"tid","val":"{{Tenant}}"}]}""")));
        Assert.Equal(HttpStatusCode.Unauthorized, (await anonymous.GetAsync("/api/studies")).StatusCode);
        using var noCsrf = factory.CreateClient();
        Assert.Equal(HttpStatusCode.BadRequest, (await noCsrf.PostAsJsonAsync("/api/attempts", new StartRequest())).StatusCode);
    }

    [Fact]
    public async Task LocalMigrationIsRepeatableAndLeavesSourceUnchanged()
    {
        string path = Path.Combine(directory, "local.db");
        var local = new TrainerStore(path);
        local.Initialize();
        var study = local.CreateStudy("Existing collection");
        var position = StudyPosition.Create(study.Id, "Personal position", Initial, notes: "Keep these notes");
        local.AddPositions(study.Id, [position]);
        var attempt = new TrainingSession(position.ToPuzzle());
        attempt.AppendNotation("e4");
        local.SaveAttempt(attempt.Snapshot());
        // Keep the source WAL pinned while parallel tests clear SQLite pools.
        using var liveSource = new Microsoft.Data.Sqlite.SqliteConnection(
            new Microsoft.Data.Sqlite.SqliteConnectionStringBuilder { DataSource = path, Pooling = false }.ToString());
        liveSource.Open();
        using var sourceTransaction = liveSource.BeginTransaction();
        await using var source = File.Open(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        byte[] sourceBefore = await SHA256.HashDataAsync(source);
        string snapshot = Path.Combine(directory, "snapshot.db");
        LocalMigration.CreateSnapshot(path, snapshot);
        byte[] header = await File.ReadAllBytesAsync(snapshot);
        Assert.Equal(new byte[] { 1, 1 }, header.Skip(18).Take(2));
        await LocalMigration.ImportAsync(Store, Owner, snapshot);
        await LocalMigration.ImportAsync(Store, Owner, snapshot);
        source.Position = 0;
        Assert.Equal(sourceBefore, await SHA256.HashDataAsync(source));
        Assert.Equal(study, Assert.Single(await Store.StudiesAsync(Owner)));
        Assert.Equal(position, Assert.Single(await Store.PositionsAsync(Owner, study.Id)));
        Assert.Single(await Store.HistoryAsync(Owner));
        Assert.Equal(position, Assert.Single(local.Positions(study.Id)));
        Assert.Single(local.History());
    }

    [Fact]
    public async Task HistoryPaginationKeepsOlderAttemptsAvailable()
    {
        var puzzle = new Puzzle("history", Initial, "e2e4 e7e5", 1000, "", "");
        var attempts = Enumerable.Range(0, 3).Select(index =>
            new TrainingSession(puzzle).Snapshot() with { StartedAt = DateTimeOffset.UtcNow.AddMinutes(index) }).ToArray();
        foreach (var attempt in attempts) await Store.SaveAttemptAsync(Owner, attempt, null);
        var first = await Store.HistoryAsync(Owner, limit: 2);
        var older = await client.GetFromJsonAsync<HistoryItem[]>("/api/attempts?offset=2&limit=2", JsonOptions);
        Assert.Equal(new[] { attempts[2].Id, attempts[1].Id }, first.Select(a => a.Id));
        Assert.Equal(attempts[0].Id, Assert.Single(older!).Id);
        Assert.Equal(HttpStatusCode.BadRequest, (await client.GetAsync("/api/attempts?offset=-1")).StatusCode);
    }

    [Fact]
    public async Task BundledChessPiecesAreServedAsRealSvgImages()
    {
        var response = await client.GetAsync("/pieces/celtic/bR.svg");
        response.EnsureSuccessStatusCode();
        Assert.Equal("image/svg+xml", response.Content.Headers.ContentType?.MediaType);
        Assert.Contains("<svg", await response.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task InvalidCsvBatchDoesNotPartiallyPersist()
    {
        var study = new Study(Guid.NewGuid(), "Atomic import");
        await Store.SaveStudyAsync(Owner, study, true);
        var position = StudyPosition.Create(study.Id, "Valid", Initial);
        await Assert.ThrowsAsync<ArgumentException>(() => Store.SavePositionsAsync(Owner, study.Id,
            [position, position with { Id = Guid.NewGuid(), Fen = "invalid" }], true));
        Assert.Empty(await Store.PositionsAsync(Owner, study.Id));
    }

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    { Converters = { new System.Text.Json.Serialization.JsonStringEnumConverter() } };

    private async Task<SessionView> Command(SessionView view, string action, string value = "")
    {
        var response = await client.PostAsJsonAsync($"/api/attempts/{view.Id}/commands", new SessionCommand(action, view.Revision, view.LineIndex, view.Cursor, value));
        response.EnsureSuccessStatusCode();
        return (await response.Content.ReadFromJsonAsync<SessionView>(JsonOptions))!;
    }

    public async Task DisposeAsync()
    {
        client.Dispose();
        await factory.DisposeAsync();
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        Directory.Delete(directory, true);
    }
}
