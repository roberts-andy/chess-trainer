using System.Buffers.Binary;
using System.Security.Claims;
using Azure.Core;
using ChessTrainer.Core;

namespace ChessTrainer.Web;

public sealed record VisionRequest(string Png, bool BlackAtBottom);
public sealed record SettingRequest(string Value);

public static class TrainerApi
{
    public static void MapTrainerApi(this WebApplication app)
    {
        var api = app.MapGroup("/api").RequireAuthorization().RequireRateLimiting("api");
        api.MapGet("/me", async (ClaimsPrincipal user, CloudAccess access, CloudStore store) =>
            new { ownerId = access.Owner(user), puzzles = await store.PuzzleCountAsync() });
        api.MapPost("/attempts", (StartRequest request, ClaimsPrincipal user, CloudTraining training) => training.StartAsync(user, request));
        api.MapGet("/attempts", (ClaimsPrincipal user, CloudAccess access, CloudStore store, int? offset, int? limit) =>
            store.HistoryAsync(access.Owner(user), offset ?? 0, limit ?? 200));
        api.MapGet("/attempts/{id:guid}", (Guid id, ClaimsPrincipal user, CloudTraining training) => training.ReadAsync(user, id));
        api.MapPost("/attempts/{id:guid}/commands", (Guid id, SessionCommand command, ClaimsPrincipal user, CloudTraining training) => training.ApplyAsync(user, id, command));
        api.MapGet("/studies", (ClaimsPrincipal user, CloudAccess access, CloudStore store) => store.StudiesAsync(access.Owner(user)));
        api.MapPost("/studies", async (Study study, ClaimsPrincipal user, CloudAccess access, CloudStore store) =>
        {
            var created = study with { Id = Guid.NewGuid() };
            await store.SaveStudyAsync(access.Owner(user), created, true);
            return created;
        });
        api.MapPut("/studies/{id:guid}", async (Guid id, Study study, ClaimsPrincipal user, CloudAccess access, CloudStore store) =>
            await store.SaveStudyAsync(access.Owner(user), study with { Id = id }, false));
        api.MapGet("/studies/{id:guid}/positions", (Guid id, ClaimsPrincipal user, CloudAccess access, CloudStore store) =>
            store.PositionsAsync(access.Owner(user), id));
        api.MapPost("/studies/{id:guid}/positions", async (Guid id, StudyPosition[] positions, ClaimsPrincipal user, CloudAccess access, CloudStore store) =>
            await store.SavePositionsAsync(access.Owner(user), id, positions.Select(p => p with { Id = Guid.NewGuid() }).ToArray(), true));
        api.MapPut("/positions/{id:guid}", async (Guid id, StudyPosition position, ClaimsPrincipal user, CloudAccess access, CloudStore store) =>
            await store.SavePositionsAsync(access.Owner(user), position.StudyId, [position with { Id = id }], false));
        api.MapGet("/settings/{name}", async (string name, ClaimsPrincipal user, CloudAccess access, CloudStore store) =>
        {
            ValidateSetting(name, "");
            return new SettingRequest(await store.SettingAsync(access.Owner(user), name) ?? "");
        });
        api.MapPut("/settings/{name}", async (string name, SettingRequest setting, ClaimsPrincipal user, CloudAccess access, CloudStore store) =>
        {
            ValidateSetting(name, setting.Value);
            await store.SaveSettingAsync(access.Owner(user), name, setting.Value);
        });
        api.MapPost("/vision", async (VisionRequest request, ClaimsPrincipal user, VisionService vision) => await vision.RecognizeAsync(user, request));
    }

    public static void ValidateSetting(string name, string value)
    {
        if (!new[] { "theme", "board-set", "piece-set" }.Contains(name) || value.Length > 40)
            throw new ArgumentException("Unsupported appearance setting.");
    }
}

public sealed class VisionService(CloudAccess access, IConfiguration configuration, TokenCredential credential, IHttpClientFactory clients)
{
    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, Queue<DateTimeOffset>> requests = new();

    public async Task<VisionPosition> RecognizeAsync(ClaimsPrincipal user, VisionRequest request)
    {
        string owner = access.Owner(user);
        if (request.Png.Length > 14 * 1024 * 1024) throw new ArgumentException("Use an image smaller than 10 MB.");
        byte[] png;
        try { png = Convert.FromBase64String(request.Png); }
        catch (FormatException) { throw new ArgumentException("Upload a valid PNG image."); }
        if (png.Length < 24 || !png.AsSpan(0, 8).SequenceEqual(new byte[] { 137, 80, 78, 71, 13, 10, 26, 10 }))
            throw new ArgumentException("Convert the screenshot to PNG before recognition.");
        uint width = BinaryPrimitives.ReadUInt32BigEndian(png.AsSpan(16, 4)), height = BinaryPrimitives.ReadUInt32BigEndian(png.AsSpan(20, 4));
        if (width == 0 || height == 0 || (ulong)width * height > 30_000_000) throw new ArgumentException("Use an image under 30 megapixels.");
        var queue = requests.GetOrAdd(owner, _ => new());
        lock (queue)
        {
            while (queue.TryPeek(out var time) && time < DateTimeOffset.UtcNow.AddHours(-1)) queue.Dequeue();
            if (queue.Count >= 20) throw new InvalidOperationException("Screenshot recognition is limited to 20 requests per hour. Try again later.");
            queue.Enqueue(DateTimeOffset.UtcNow);
        }
        using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(3));
        using var client = clients.CreateClient();
        client.Timeout = TimeSpan.FromMinutes(3);
        return await new AzureVisionRecognizer(client, credential).RecognizeAsync(png,
            configuration["Vision:Endpoint"] ?? throw new InvalidOperationException("Configure the Azure vision endpoint."),
            configuration["Vision:Deployment"] ?? "chess-vision", request.BlackAtBottom, timeout.Token);
    }
}
