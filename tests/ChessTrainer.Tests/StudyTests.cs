using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using ChessTrainer.Core;
using Xunit;

namespace ChessTrainer.Tests;

public sealed class StudyTests : IDisposable
{
    private const string Initial = "rnbqkbnr/pppppppp/8/8/8/8/PPPPPPPP/RNBQKBNR w KQkq - 0 1";
    private readonly string directory = Path.Combine(Path.GetTempPath(), "ChessTrainer.Tests", Guid.NewGuid().ToString("N"));
    private readonly TrainerStore store;

    public StudyTests()
    {
        store = new TrainerStore(Path.Combine(directory, "test.db"));
        store.Initialize();
    }

    [Theory]
    [InlineData(SolveMode.Board)]
    [InlineData(SolveMode.Notation)]
    [InlineData(SolveMode.Visualization)]
    public void StudyPositionStartsAtExactFenAndDoesNotInventCorrectness(SolveMode mode)
    {
        var position = StudyPosition.Create(Guid.NewGuid(), "Opening", Initial);
        var session = new TrainingSession(position.ToPuzzle(), mode);
        Assert.Equal(Initial, session.StartingFen);
        session.AppendNotation("e4 e5");
        Assert.Null(session.Results);
        Assert.Equal(mode == SolveMode.Visualization, session.DisplayFen == Initial);
        Assert.Equal(Verdict.Unverified, session.Submit().Results![0].Verdict);
        Assert.Empty(session.SolutionNotation());
    }

    [Fact]
    public void ReferenceSolutionIsOptionalLegalAndStartsWithTheFirstEnteredMove()
    {
        var position = StudyPosition.Create(Guid.NewGuid(), "Opening", Initial, "1.e4 e5 2.Nf3");
        Assert.Equal("e2e4 e7e5 g1f3", position.Solution);
        Assert.Throws<ArgumentException>(() => StudyPosition.Create(position.StudyId, "Bad", Initial, "e4 nonsense"));
        var session = new TrainingSession(position.ToPuzzle());
        session.AppendNotation("e4 e5 Nf3");
        Assert.Equal(Verdict.Correct, session.Submit().Results![0].Verdict);
    }

    [Theory]
    [InlineData("8/8/8/8/8/8/8/8 w - - 0 1")]
    [InlineData("4k3/8/8/8/8/8/8/4K4 w - - 0 1")]
    [InlineData("4k3/8/8/8/8/8/8/4K3 x - - 0 1")]
    [InlineData("4k3/8/8/8/8/8/8/4K3 w K - 0 1")]
    [InlineData("4k3/8/8/8/8/8/4k3/4K3 w - - 0 1")]
    [InlineData("4k3/8/8/8/8/8/8/4K3 w - d3 0 1")]
    [InlineData("4k3/8/8/8/8/8/8/4K3 w - - -1 1")]
    [InlineData("4k3/8/8/8/8/8/8/4K3 w - - 0 0")]
    [InlineData("4k3/8/8/8/8/8/8/4K3")]
    public void MalformedPositionsAreRejected(string fen) =>
        Assert.Throws<ArgumentException>(() => StudyPosition.ValidateFen(fen));

    [Fact]
    public void StudiesPositionsAndReviewAnnotationsSurviveReopeningAndMoving()
    {
        var study = store.CreateStudy("Openings");
        var destination = store.CreateStudy("Review");
        var position = StudyPosition.Create(study.Id, "King pawn", Initial, "", "Control the center.");
        store.AddPositions(study.Id, [position]);
        store.RenameStudy(study.Id, "Opening repertoire");
        store.MovePosition(position.Id, destination.Id);
        Assert.Empty(store.Positions(study.Id));
        Assert.Throws<InvalidOperationException>(() => store.MovePosition(position.Id, Guid.NewGuid()));
        var session = new TrainingSession(position.ToPuzzle());
        session.Annotate("Starting plan", "");
        session.AppendNotation("e4 e5");
        session.Submit();
        session.Annotate("Review note after submitting", "?!");
        store.SaveAttempt(session.Snapshot());
        var reopened = new TrainerStore(Path.Combine(directory, "test.db"));
        reopened.Initialize();
        Assert.Contains(reopened.Studies(), s => s.Name == "Opening repertoire");
        Assert.Equal(position with { StudyId = destination.Id }, Assert.Single(reopened.Positions(destination.Id)));
        var restored = new TrainingSession(reopened.ReadAttempt(session.Id));
        restored.Navigate(2);
        Assert.Equal(new MoveAnnotation("Review note after submitting", "?!"), restored.CurrentAnnotation);
        Assert.Equal(Verdict.Unverified, restored.Results![0].Verdict);
        restored.Navigate(0);
        Assert.Equal("Starting plan", restored.CurrentAnnotation.Comment);
        Assert.True(Assert.Single(reopened.History()).IsCustom);
    }

    [Fact]
    public void SavingEditsOverwritesTheSamePositionAndSurvivesReopening()
    {
        var study = store.CreateStudy("Openings");
        var position = StudyPosition.Create(study.Id, "Original", Initial);
        var other = StudyPosition.Create(study.Id, "Keep this", Initial);
        store.AddPositions(study.Id, [position, other]);
        var edited = position with
        {
            Title = "Updated",
            Fen = "4k3/8/8/8/8/8/4P3/4K3 w - - 0 1",
            Solution = "e4",
            Notes = "New starting notes"
        };
        store.UpdatePosition(edited);
        store.UpdatePosition(edited with { Notes = "Revised again" });

        var reopened = new TrainerStore(Path.Combine(directory, "test.db"));
        reopened.Initialize();
        var positions = reopened.Positions(study.Id);
        Assert.Equal(2, positions.Count);
        Assert.Equal(edited with { Solution = "e2e4", Notes = "Revised again" }, positions[0]);
        Assert.Equal(other, positions[1]);
        Assert.Single(reopened.Studies());
    }

    [Fact]
    public void InvalidOrMissingPositionEditsDoNotInsertOrChangePositions()
    {
        var study = store.CreateStudy("Openings");
        var otherStudy = store.CreateStudy("Other study");
        var position = StudyPosition.Create(study.Id, "Original", Initial);
        store.AddPositions(study.Id, [position]);
        Assert.Throws<ArgumentException>(() => store.UpdatePosition(position with { Fen = "bad" }));
        Assert.Throws<ArgumentException>(() => store.UpdatePosition(position with { Solution = "e5" }));
        Assert.Throws<InvalidOperationException>(() => store.UpdatePosition(position with { Id = Guid.NewGuid() }));
        Assert.Throws<InvalidOperationException>(() => store.UpdatePosition(position with { StudyId = otherStudy.Id }));
        Assert.Equal(position, Assert.Single(store.Positions(study.Id)));
        Assert.Empty(store.Positions(otherStudy.Id));
    }

    [Fact]
    public void CsvSupportsQuotedMultilineNotesAndFailedBatchesAreAtomic()
    {
        var study = store.CreateStudy("CSV imports");
        using var valid = new MemoryStream(Encoding.UTF8.GetBytes($"title,fen,solution,notes\n\"Opening, one\",\"{Initial}\",e4,\"First line\nSecond line\"\n"));
        var positions = StudyPosition.ReadCsv(valid, study.Id);
        store.AddPositions(study.Id, positions);
        Assert.Equal("First line\nSecond line", Assert.Single(store.Positions(study.Id)).Notes);
        using var invalid = new MemoryStream(Encoding.UTF8.GetBytes($"FEN\n\"{Initial}\"\ninvalid"));
        Assert.Throws<InvalidDataException>(() => StudyPosition.ReadCsv(invalid, study.Id));
        Assert.Single(store.Positions(study.Id));
        Assert.Throws<ArgumentException>(() => store.AddPositions(study.Id,
            [positions[0] with { Id = Guid.NewGuid() }, positions[0] with { Id = Guid.NewGuid(), Fen = "bad" }]));
        Assert.Single(store.Positions(study.Id));
        Assert.Equal(0, store.PuzzleCount());
    }

    [Fact]
    public void AnnotationSnapshotsAreIndependentAndTrimmingRemovesOnlyDiscardedMoveNotes()
    {
        var session = new TrainingSession(StudyPosition.Create(Guid.NewGuid(), "Opening", Initial).ToPuzzle());
        session.AppendNotation("e4 e5");
        session.Annotate("Discarded response", "?");
        session.Navigate(1);
        session.Annotate("Shared plan", "!");
        var snapshot = session.Snapshot();
        session.BranchOpponentResponse();
        Assert.Equal("Shared plan", session.CurrentAnnotation.Comment);
        session.Annotate("Independent branch", "!?");
        Assert.Equal("Shared plan", session.Lines[0].Annotations[1].Comment);
        Assert.Equal("Shared plan", snapshot.Lines[0].Annotations[1].Comment);
        session.SelectLine(0);
        session.Navigate(1);
        session.TrimAfterCursor();
        Assert.False(session.ActiveLine.Annotations.ContainsKey(2));
        Assert.Equal("Shared plan", session.CurrentAnnotation.Comment);
        session.Annotate("", "");
        Assert.Empty(session.ActiveLine.Annotations);
    }

    [Fact]
    public void ExistingAttemptJsonDefaultsToLichessWithNoAnnotations()
    {
        var puzzle = new Puzzle("legacy", Initial, "e2e4 e7e5", 1000, "", "");
        var json = JsonSerializer.SerializeToNode(new TrainingSession(puzzle).Snapshot())!;
        foreach (string field in new[] { "IsCustom", "Title", "StudyId" }) json["Puzzle"]!.AsObject().Remove(field);
        json["Lines"]![0]!.AsObject().Remove("Annotations");
        var restored = new TrainingSession(json.Deserialize<Attempt>()!);
        Assert.False(restored.Puzzle.IsCustom);
        Assert.Equal('P', restored.CurrentPosition.PieceAt("e4"));
        Assert.Empty(restored.ActiveLine.Annotations);
    }

    public void Dispose()
    {
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        Directory.Delete(directory, true);
    }
}
