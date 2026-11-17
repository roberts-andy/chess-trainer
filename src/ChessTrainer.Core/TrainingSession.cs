using System.Text.Json.Serialization;

namespace ChessTrainer.Core;

public enum SolveMode { Board, Notation, Visualization }
public enum Verdict { Correct, Incorrect, Incomplete, Unverified }

public sealed record Puzzle(string Id, string Fen, string Moves, int Rating, string Themes, string GameUrl,
    bool IsCustom = false, string Title = "", Guid? StudyId = null)
{
    [JsonIgnore] public string[] AllMoves => Moves.Split(' ', StringSplitOptions.RemoveEmptyEntries);
    [JsonIgnore] public string[] Solution => IsCustom ? AllMoves : AllMoves[1..];
    [JsonIgnore] public string StartingFen
    {
        get
        {
            if (IsCustom) return Fen;
            var position = new ChessPosition(Fen);
            position.PlayUci(AllMoves[0]);
            return position.Fen;
        }
    }
}

public sealed class AnalysisLine
{
    public string Name { get; set; } = "Main line";
    public int BranchPly { get; set; }
    public List<string> Moves { get; set; } = [];
    public List<string> San { get; set; } = [];
    public Dictionary<int, MoveAnnotation> Annotations { get; set; } = [];
}

public sealed record MoveAnnotation(string Comment, string Symbol);
public sealed record LineResult(string Name, Verdict Verdict, string Explanation);
public sealed record Attempt(Guid Id, Puzzle Puzzle, DateTimeOffset StartedAt, DateTimeOffset? SubmittedAt,
    SolveMode Mode, IReadOnlyList<AnalysisLine> Lines, IReadOnlyList<LineResult>? Results);

public sealed class TrainingSession
{
    public Puzzle Puzzle { get; }
    public DateTimeOffset StartedAt { get; }
    public Guid Id { get; }
    public SolveMode Mode { get; set; }
    public List<AnalysisLine> Lines { get; }
    public int ActiveLineIndex { get; private set; }
    public int Cursor { get; private set; }
    public bool Submitted { get; private set; }
    public IReadOnlyList<LineResult>? Results { get; private set; }
    public DateTimeOffset? SubmittedAt { get; private set; }
    public AnalysisLine ActiveLine => Lines[ActiveLineIndex];
    public string StartingFen { get; }
    public ChessPosition CurrentPosition => PositionAt(Cursor);
    public string DisplayFen => Mode == SolveMode.Visualization && !Submitted ? StartingFen : CurrentPosition.Fen;

    public TrainingSession(Puzzle puzzle, SolveMode mode = SolveMode.Board)
    {
        Puzzle = puzzle;
        Mode = mode;
        StartingFen = puzzle.StartingFen;
        StartedAt = DateTimeOffset.UtcNow;
        Id = Guid.NewGuid();
        Lines = [new AnalysisLine()];
    }

    public TrainingSession(Attempt attempt)
    {
        Puzzle = attempt.Puzzle;
        Mode = attempt.Mode;
        StartingFen = Puzzle.StartingFen;
        StartedAt = attempt.StartedAt;
        Id = attempt.Id;
        Lines = attempt.Lines.Select(CloneLine).ToList();
        SubmittedAt = attempt.SubmittedAt;
        Submitted = SubmittedAt.HasValue;
        Results = attempt.Results;
    }

    public void Navigate(int ply) => Cursor = Math.Clamp(ply, 0, ActiveLine.Moves.Count);

    public void SelectLine(int index)
    {
        if (index < 0 || index >= Lines.Count) throw new ArgumentOutOfRangeException(nameof(index));
        ActiveLineIndex = index;
        Cursor = ActiveLine.Moves.Count;
    }

    public void AppendUci(string uci)
    {
        EnsureEditableEnd();
        var position = CurrentPosition;
        string san = position.PlayUci(uci);
        ActiveLine.Moves.Add(uci.ToLowerInvariant());
        ActiveLine.San.Add(san);
        Cursor++;
    }

    public void AppendNotation(string notation)
    {
        EnsureEditableEnd();
        string[] tokens = ChessPosition.Tokenize(notation);
        if (tokens.Length == 0) throw new ArgumentException("Enter at least one move.");
        var position = CurrentPosition;
        // Parse the entire entry first so an invalid token cannot leave a partial edit.
        var parsed = tokens.Select(position.PlayNotation).ToArray();
        ActiveLine.Moves.AddRange(parsed.Select(p => p.Uci));
        ActiveLine.San.AddRange(parsed.Select(p => p.San));
        Cursor += parsed.Length;
    }

    public void BranchOpponentResponse()
    {
        EnsureEditable();
        if (Cursor == 0 || Cursor % 2 == 0)
            throw new InvalidOperationException("Select the position after one of your moves, before an opponent response.");
        var branch = new AnalysisLine
        {
            Name = $"Variation {Lines.Count}",
            BranchPly = Cursor,
            Moves = ActiveLine.Moves.Take(Cursor).ToList(),
            San = ActiveLine.San.Take(Cursor).ToList(),
            Annotations = ActiveLine.Annotations.Where(a => a.Key <= Cursor).ToDictionary()
        };
        Lines.Add(branch);
        ActiveLineIndex = Lines.Count - 1;
    }

    public void TrimAfterCursor()
    {
        EnsureEditable();
        if (Cursor < ActiveLine.BranchPly)
            throw new InvalidOperationException("A variation's shared prefix is fixed. Select its parent line to edit earlier moves.");
        ActiveLine.Moves.RemoveRange(Cursor, ActiveLine.Moves.Count - Cursor);
        ActiveLine.San.RemoveRange(Cursor, ActiveLine.San.Count - Cursor);
        foreach (int ply in ActiveLine.Annotations.Keys.Where(p => p > Cursor).ToArray())
            ActiveLine.Annotations.Remove(ply);
    }

    public void RemoveActiveVariation()
    {
        EnsureEditable();
        if (ActiveLineIndex == 0) throw new InvalidOperationException("The main line cannot be removed.");
        Lines.RemoveAt(ActiveLineIndex);
        SelectLine(0);
    }

    public Attempt Submit()
    {
        EnsureEditable();
        Results = Lines.Select(Evaluate).ToArray();
        SubmittedAt = DateTimeOffset.UtcNow;
        Submitted = true;
        return Snapshot();
    }

    public Attempt Snapshot() => new(Id, Puzzle, StartedAt, SubmittedAt, Mode,
        Lines.Select(CloneLine).ToArray(), Results?.ToArray());

    public MoveAnnotation CurrentAnnotation => ActiveLine.Annotations.GetValueOrDefault(Cursor) ?? new("", "");

    public void Annotate(string comment, string symbol)
    {
        if (!new[] { "", "!", "?", "!!", "??", "!?", "?!" }.Contains(symbol))
            throw new ArgumentException("Choose a supported move annotation.");
        if (string.IsNullOrWhiteSpace(comment) && symbol.Length == 0) ActiveLine.Annotations.Remove(Cursor);
        else ActiveLine.Annotations[Cursor] = new(comment, symbol);
    }

    public string SolutionNotation()
    {
        if (!Submitted) throw new InvalidOperationException("Submit before revealing the solution.");
        var position = new ChessPosition(StartingFen);
        return string.Join(" ", Puzzle.Solution.Select(position.PlayUci));
    }

    private ChessPosition PositionAt(int ply)
    {
        var position = new ChessPosition(StartingFen);
        foreach (string uci in ActiveLine.Moves.Take(ply)) position.PlayUci(uci);
        return position;
    }

    private LineResult Evaluate(AnalysisLine line)
    {
        string[] expected = Puzzle.Solution;
        if (Puzzle.IsCustom && expected.Length == 0)
            return new(line.Name, Verdict.Unverified, "Analysis saved. This position has no reference solution; correctness has not been assessed.");
        var position = new ChessPosition(StartingFen);
        for (int i = 0; i < line.Moves.Count; i++)
        {
            position.PlayUci(line.Moves[i]);
            bool matches = i < expected.Length && line.Moves[i] == expected[i];
            // All mating first moves win a mate-in-one, not just Lichess's chosen move.
            if (i % 2 == 0 && position.IsCheckmate && i == line.Moves.Count - 1)
                return new(line.Name, Verdict.Correct, "Your line ends in checkmate.");
            if (!matches)
                return i % 2 == 1
                    ? new(line.Name, Verdict.Unverified, "Alternate opponent response. Saved for review; the database has no solution for this branch.")
                    : new(line.Name, Verdict.Incorrect, $"Your move at ply {i + 1} differs from the reference solution.");
        }
        return line.Moves.Count == expected.Length
            ? new(line.Name, Verdict.Correct, "Matches the complete reference solution.")
            : new(line.Name, Verdict.Incomplete, "The entered line stops before the reference solution ends.");
    }

    private void EnsureEditable()
    {
        if (Submitted) throw new InvalidOperationException("This attempt has been submitted and is read-only.");
    }

    private void EnsureEditableEnd()
    {
        EnsureEditable();
        if (Cursor != ActiveLine.Moves.Count)
            throw new InvalidOperationException("Go to the end, trim after this position, or create a variation before adding moves.");
    }

    private static AnalysisLine CloneLine(AnalysisLine line) => new()
    {
        Name = line.Name, BranchPly = line.BranchPly,
        Moves = [.. line.Moves], San = [.. line.San], Annotations = new(line.Annotations)
    };
}
