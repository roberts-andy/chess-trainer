using System.Security.Claims;
using ChessTrainer.Core;

namespace ChessTrainer.Web;

public sealed record StartRequest(int Minimum = 800, int Maximum = 2000, string? ExcludeId = null, Guid? PositionId = null, SolveMode Mode = SolveMode.Board);
public sealed record SessionCommand(string Action, long Revision, int LineIndex = 0, int Cursor = 0, string Value = "", string Symbol = "");
public sealed record SessionView(Guid Id, long Revision, string PuzzleId, string Title, int Rating, bool IsCustom,
    string Fen, string StartingFen, SolveMode Mode, int LineIndex, int Cursor, IReadOnlyList<AnalysisLine> Lines,
    bool Submitted, IReadOnlyList<LineResult>? Results, string? Solution);

public sealed class CloudAccess(IConfiguration configuration)
{
    public string Owner(ClaimsPrincipal user)
    {
        string? oid = user.FindFirstValue("oid");
        string? tenant = user.FindFirstValue("tid");
        if (user.Identity?.IsAuthenticated != true ||
            !Guid.TryParse(oid, out var owner) || !Guid.TryParse(tenant, out var tenantId) ||
            !Guid.TryParse(configuration["Authentication:OwnerId"], out var allowedOwner) ||
            !Guid.TryParse(configuration["Authentication:TenantId"], out var allowedTenant) ||
            owner != allowedOwner || tenantId != allowedTenant)
            throw new UnauthorizedAccessException("Sign in with the account configured for this trainer.");
        return owner.ToString();
    }
}

public sealed class CloudTraining(CloudStore store, CloudAccess access)
{
    public async Task<SessionView> StartAsync(ClaimsPrincipal user, StartRequest request)
    {
        string owner = access.Owner(user);
        if (!Enum.IsDefined(request.Mode)) throw new ArgumentException("Choose a supported practice mode.");
        Puzzle puzzle;
        string notes = "";
        if (request.PositionId is Guid id)
        {
            var position = await store.PositionAsync(owner, id);
            puzzle = position.ToPuzzle();
            notes = position.Notes;
        }
        else puzzle = await store.RandomPuzzleAsync(request.Minimum, request.Maximum, request.ExcludeId)
            ?? throw new InvalidOperationException("No puzzles match this rating range.");
        var session = new TrainingSession(puzzle, request.Mode);
        if (!string.IsNullOrWhiteSpace(notes)) session.Annotate(notes, "");
        await store.SaveAttemptAsync(owner, session.Snapshot(), null);
        return View(session, 0);
    }

    public async Task<SessionView> ReadAsync(ClaimsPrincipal user, Guid id)
    {
        var saved = await store.AttemptAsync(access.Owner(user), id);
        var session = new TrainingSession(saved.Attempt);
        session.SelectLine(0);
        return View(session, saved.Revision);
    }

    public async Task<SessionView> ApplyAsync(ClaimsPrincipal user, Guid id, SessionCommand command)
    {
        string owner = access.Owner(user);
        var saved = await store.AttemptAsync(owner, id);
        if (saved.Revision != command.Revision) throw new EditConflictException();
        var session = new TrainingSession(saved.Attempt);
        session.SelectLine(command.LineIndex);
        if (command.Cursor < 0 || command.Cursor > session.ActiveLine.Moves.Count) throw new ArgumentException("Select an existing move.");
        session.Navigate(command.Cursor);
        if (command.Value.Length > 50000 || command.Symbol.Length > 2) throw new ArgumentException("Move or comment text is too long.");
        switch (command.Action)
        {
            case "notation": session.AppendNotation(command.Value); break;
            case "uci": session.AppendUci(command.Value); break;
            case "branch": session.BranchOpponentResponse(); break;
            case "trim": session.TrimAfterCursor(); break;
            case "remove": session.RemoveActiveVariation(); break;
            case "submit": session.Submit(); break;
            case "annotation": session.Annotate(command.Value, command.Symbol); break;
            case "mode":
                if (!Enum.TryParse<SolveMode>(command.Value, out var mode) || !Enum.IsDefined(mode)) throw new ArgumentException("Choose a supported mode.");
                session.Mode = mode;
                break;
            case "navigate": break;
            default: throw new ArgumentException("Unknown analysis action.");
        }
        await store.SaveAttemptAsync(owner, session.Snapshot(), saved.Revision);
        return View(session, saved.Revision + 1);
    }

    private static SessionView View(TrainingSession session, long revision) => new(session.Id, revision,
        session.Puzzle.Id, session.Puzzle.IsCustom ? session.Puzzle.Title : $"{(new ChessPosition(session.StartingFen).WhiteToMove ? "White" : "Black")} to solve",
        session.Puzzle.Rating, session.Puzzle.IsCustom, session.DisplayFen, session.StartingFen, session.Mode,
        session.ActiveLineIndex, session.Cursor, session.Snapshot().Lines, session.Submitted, session.Results,
        session.Submitted ? session.SolutionNotation() : null);
}
