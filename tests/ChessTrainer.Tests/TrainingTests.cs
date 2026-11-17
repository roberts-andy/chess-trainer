using ChessTrainer.Core;
using Xunit;

namespace ChessTrainer.Tests;

public sealed class TrainingTests
{
    private static readonly Puzzle MatePuzzle = new("00sHx",
        "q3k1nr/1pp1nQpp/3p4/1P2p3/4P3/B1PP1b2/B5PP/5K2 b k - 0 17",
        "e8d7 a2e6 d7d8 f7f8", 1760, "mate mateIn2", "https://lichess.org/yyznGmXs/black#34");
    private const string Initial = "rnbqkbnr/pppppppp/8/8/8/8/PPPPPPPP/RNBQKBNR w KQkq - 0 1";

    [Fact]
    public void SetupMoveIsAppliedBeforeSolving()
    {
        var session = new TrainingSession(MatePuzzle);
        Assert.Equal('k', session.CurrentPosition.PieceAt("d7"));
        Assert.Equal(' ', session.CurrentPosition.PieceAt("e8"));
        Assert.True(session.CurrentPosition.WhiteToMove);
        Assert.Equal(0, session.Cursor);
        Assert.Empty(session.ActiveLine.Moves);
    }

    [Theory]
    [InlineData(SolveMode.Board)]
    [InlineData(SolveMode.Notation)]
    [InlineData(SolveMode.Visualization)]
    public void AllModesRecordBothColorsAndDoNotGradeUntilSubmit(SolveMode mode)
    {
        var session = new TrainingSession(MatePuzzle, mode);
        string start = session.DisplayFen;
        if (mode == SolveMode.Board)
            foreach (string uci in MatePuzzle.Solution) session.AppendUci(uci);
        else session.AppendNotation("17. Be6+ Kd8 18.Qf8#");
        Assert.Equal(MatePuzzle.Solution, session.ActiveLine.Moves);
        Assert.Null(session.Results);
        Assert.False(session.Submitted);
        Assert.Throws<InvalidOperationException>(() => session.SolutionNotation());
        Assert.Equal(mode == SolveMode.Visualization, start == session.DisplayFen);
        Attempt result = session.Submit();
        Assert.Equal(Verdict.Correct, result.Results![0].Verdict);
        Assert.Contains("Qf8#", session.SolutionNotation());
        Assert.Throws<InvalidOperationException>(() => session.AppendNotation("Kd7"));
    }

    [Fact]
    public void NotationEntryIsAtomicAndSupportsUci()
    {
        var session = new TrainingSession(MatePuzzle, SolveMode.Notation);
        Assert.Throws<ArgumentException>(() => session.AppendNotation("Be6+ NotAMove"));
        Assert.Empty(session.ActiveLine.Moves);
        session.AppendNotation("a2e6 d7d8 f7f8");
        Assert.Equal(["Be6+", "Kd8", "Qf8#"], session.ActiveLine.San);
    }

    [Fact]
    public void AlternativeOpponentLinePreservesMainAndIsUnverified()
    {
        var puzzle = new Puzzle("branch", Initial, "e2e4 e7e5 g1f3 b8c6", 1000, "", "");
        var session = new TrainingSession(puzzle);
        session.AppendNotation("e5 Nf3 Nc6");
        session.Navigate(1);
        session.BranchOpponentResponse();
        Assert.Single(session.ActiveLine.Moves);
        string other = session.CurrentPosition.LegalMoves().First(m => m != "g1f3");
        session.AppendUci(other);
        Assert.Equal(3, session.Lines[0].Moves.Count);
        Assert.Null(session.Results);
        Attempt attempt = session.Submit();
        Assert.Equal(Verdict.Correct, attempt.Results![0].Verdict);
        Assert.Equal(Verdict.Unverified, attempt.Results[1].Verdict);
        Assert.Equal(1, attempt.Lines[1].BranchPly);
    }

    [Fact]
    public void OnlyOpponentResponsesCanBeBranchedAndSharedPrefixCannotBeTrimmed()
    {
        var session = new TrainingSession(MatePuzzle);
        Assert.Throws<InvalidOperationException>(session.BranchOpponentResponse);
        session.AppendUci("a2e6");
        session.BranchOpponentResponse();
        session.Navigate(0);
        Assert.Throws<InvalidOperationException>(session.TrimAfterCursor);
    }

    [Fact]
    public void IncompleteAndIncorrectAreOnlyDeterminedOnSubmit()
    {
        var incomplete = new TrainingSession(MatePuzzle);
        incomplete.AppendNotation("Be6+");
        Assert.Equal(Verdict.Incomplete, incomplete.Submit().Results![0].Verdict);
        var incorrect = new TrainingSession(MatePuzzle);
        string other = incorrect.CurrentPosition.LegalMoves().First(m => m != "a2e6");
        incorrect.AppendUci(other);
        Assert.Null(incorrect.Results);
        Assert.Equal(Verdict.Incorrect, incorrect.Submit().Results![0].Verdict);
    }

    [Fact]
    public void AnyImmediateCheckmateWinsEvenIfNotPublishedMove()
    {
        var puzzle = new Puzzle("mate1", "8/5K1k/6Q1/8/8/8/8/8 b - - 0 1",
            "h7h8 g6g7", 1000, "mateIn1", "");
        var session = new TrainingSession(puzzle);
        session.AppendUci("g6g7");
        Assert.Equal(Verdict.Correct, session.Submit().Results![0].Verdict);

        var position = new ChessPosition(puzzle.StartingFen);
        string alternate = position.LegalMoves().First(m =>
        {
            var copy = new ChessPosition(position.Fen);
            copy.PlayUci(m);
            return m != "g6g7" && copy.IsCheckmate;
        });
        var alternative = new TrainingSession(puzzle);
        alternative.AppendUci(alternate);
        Assert.Equal(Verdict.Correct, alternative.Submit().Results![0].Verdict);
    }

    [Fact]
    public void InitialPositionHas400LegalTwoPlyContinuations()
    {
        var position = new ChessPosition(Initial);
        Assert.Equal(20, position.LegalMoves().Count);
        int count = 0;
        foreach (string move in position.LegalMoves())
        {
            var copy = new ChessPosition(Initial);
            copy.PlayUci(move);
            count += copy.LegalMoves().Count;
        }
        Assert.Equal(400, count);
    }

    [Fact]
    public void CastlingEnPassantUnderpromotionAndPinsAreHandled()
    {
        var castle = new ChessPosition("r3k2r/8/8/8/8/8/8/R3K2R w KQkq - 0 1");
        Assert.Equal("O-O", castle.PlayUci("e1g1"));
        Assert.Equal('R', castle.PieceAt("f1"));
        Assert.Equal(' ', castle.PieceAt("h1"));
        var ep = new ChessPosition("4k3/8/8/3pP3/8/8/8/4K3 w - d6 0 1");
        Assert.Equal("exd6", ep.PlayUci("e5d6"));
        Assert.Equal(' ', ep.PieceAt("d5"));
        var promotion = new ChessPosition("7k/P7/8/8/8/8/8/4K3 w - - 0 1");
        Assert.Equal("a8=N", promotion.PlayUci("a7a8n"));
        Assert.Equal('N', promotion.PieceAt("a8"));
        var pin = new ChessPosition("4r1k1/8/8/8/8/8/4R3/4K3 w - - 0 1");
        Assert.Throws<ArgumentException>(() => pin.PlayUci("e2d2"));
    }

    [Fact]
    public void NavigationRequiresExplicitTrimAndSnapshotsDoNotMutate()
    {
        var session = new TrainingSession(MatePuzzle);
        session.AppendNotation("Be6+ Kd8 Qf8#");
        Attempt saved = session.Snapshot();
        session.Navigate(1);
        Assert.Throws<InvalidOperationException>(() => session.AppendUci("d7d8"));
        session.TrimAfterCursor();
        Assert.Single(session.ActiveLine.Moves);
        Assert.Equal(3, saved.Lines[0].Moves.Count);
        session.AppendNotation("Kd8 Qf8#");
        Assert.Equal(3, session.Cursor);
    }
}
