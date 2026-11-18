using System.Text.Json;
using ChessTrainer.Core;
using Microsoft.Data.Sqlite;
using Xunit;

namespace ChessTrainer.Tests;

public sealed class DesktopPersistenceTests : IDisposable
{
    private static readonly Puzzle TestPuzzle = new("00sHx",
        "q3k1nr/1pp1nQpp/3p4/1P2p3/4P3/B1PP1b2/B5PP/5K2 b k - 0 17",
        "e8d7 a2e6 d7d8 f7f8", 1760, "mate mateIn2", "");
    private readonly string path = Path.Combine(AppContext.BaseDirectory,
        $"DesktopPersistenceTests-{Guid.NewGuid():N}", "trainer.db");

    [Theory]
    [InlineData("notation")]
    [InlineData("board")]
    [InlineData("annotation")]
    [InlineData("submit")]
    [InlineData("mode")]
    [InlineData("branch")]
    [InlineData("trim")]
    [InlineData("delete")]
    public void FailedSaveLeavesPublishedAndDurableStateUnchangedAndCanBeRetried(string operation)
    {
        var store = CreateStore();
        var current = new TrainingSession(TestPuzzle, operation == "board" ? SolveMode.Board : SolveMode.Visualization);
        current.AppendNotation("Be6+");
        current.BranchOpponentResponse();
        if (operation is not ("notation" or "board" or "branch"))
        {
            current.AppendNotation("Kd8 Qf8#");
            current.Navigate(1);
        }
        var original = current;
        var before = JsonSerializer.Serialize(current.Snapshot());
        int cursor = current.Cursor;
        long revision = store.SaveAttempt(current.Snapshot(), null);
        Action<TrainingSession> edit = operation switch
        {
            "notation" => s => s.AppendNotation("Kd8 Qf8#"),
            "board" => s => s.AppendUci("d7d8"),
            "annotation" => s => s.Annotate("Retry this text", "!?"),
            "submit" => s => s.Submit(),
            "mode" => s => s.Mode = SolveMode.Notation,
            "branch" => s => s.BranchOpponentResponse(),
            "trim" => s => s.TrimAfterCursor(),
            "delete" => s => s.RemoveActiveVariation(),
            _ => throw new ArgumentOutOfRangeException(nameof(operation))
        };
        void Change() => current = AttemptEdit.Commit(current, edit,
            candidate => revision = store.SaveAttempt(candidate, revision));

        Execute("""
            CREATE TRIGGER RejectAttemptSave BEFORE UPDATE ON Attempts
            BEGIN SELECT RAISE(ABORT, 'forced write failure'); END
            """);
        Assert.Throws<SqliteException>(Change);
        Assert.Same(original, current);
        Assert.Equal(1, current.ActiveLineIndex);
        Assert.Equal(cursor, current.Cursor);
        Assert.Equal(before, JsonSerializer.Serialize(current.Snapshot()));
        Assert.False(current.Submitted);
        Assert.Null(current.Results);
        Assert.Throws<InvalidOperationException>(() => current.SolutionNotation());
        var reopened = new TrainerStore(path).ReadAttemptForEdit(current.Id);
        Assert.Equal(before, JsonSerializer.Serialize(reopened.Attempt));
        Assert.Equal(revision, reopened.Revision);

        Execute("DROP TRIGGER RejectAttemptSave");
        Change();
        Assert.NotSame(original, current);
        Assert.Equal(1L, revision);
        Assert.Equal(operation == "delete" ? 0 : operation == "branch" ? 2 : 1, current.ActiveLineIndex);
        if (operation is "annotation" or "submit" or "mode" or "branch" or "trim")
            Assert.Equal(cursor, current.Cursor);
        if (operation == "annotation")
            Assert.Equal(new MoveAnnotation("Retry this text", "!?"), current.CurrentAnnotation);
        if (operation == "submit")
            Assert.Contains("Qf8#", current.SolutionNotation());
        Assert.Equal(JsonSerializer.Serialize(current.Snapshot()), JsonSerializer.Serialize(store.ReadAttempt(current.Id)));
    }

    [Fact]
    public void StaleWriterCannotOverwriteSubmissionAndReviewDoesNotWrite()
    {
        var first = CreateStore();
        var second = new TrainerStore(path);
        var active = new TrainingSession(TestPuzzle);
        first.SaveAttempt(active.Snapshot());
        var stale = second.ReadAttemptForEdit(active.Id);
        active.AppendNotation("Be6+ Kd8 Qf8#");
        long committed = first.SaveAttempt(active.Submit(), stale.Revision);

        var oldDraft = new TrainingSession(stale.Attempt);
        oldDraft.AppendNotation("Be6+");
        Assert.Throws<AttemptConflictException>(() => second.SaveAttempt(oldDraft.Snapshot(), stale.Revision));
        Assert.Throws<AttemptConflictException>(() => second.SaveAttempt(oldDraft.Snapshot()));
        Assert.NotNull(Assert.Single(second.History()).SubmittedAt);

        var fresh = second.ReadAttemptForEdit(active.Id);
        var review = new TrainingSession(fresh.Attempt);
        review.SelectLine(0);
        review.Navigate(1);
        Assert.Equal(committed, second.ReadAttemptForEdit(active.Id).Revision);
        Assert.Equal(TestPuzzle.Solution, review.ActiveLine.Moves);
        Assert.Equal(Verdict.Correct, review.Results![0].Verdict);
        Assert.Throws<InvalidOperationException>(() => second.SaveAttempt(
            fresh.Attempt with { SubmittedAt = null, Results = null }, committed));
        review.ActiveLine.Moves[0] = "g6g7";
        Assert.Throws<InvalidOperationException>(() => second.SaveAttempt(review.Snapshot(), committed));

        review = new TrainingSession(fresh.Attempt);
        review.Navigate(1);
        var annotated = AttemptEdit.Commit(review, s => s.Annotate("Review note", "!"),
            attempt => committed = second.SaveAttempt(attempt, committed));
        Assert.Equal(new MoveAnnotation("Review note", "!"), annotated.CurrentAnnotation);
        Assert.Throws<AttemptConflictException>(() => first.SaveAttempt(fresh.Attempt, fresh.Revision));
        var final = first.ReadAttemptForEdit(active.Id);
        Assert.Equal(committed, final.Revision);
        Assert.Equal(fresh.Attempt.SubmittedAt, final.Attempt.SubmittedAt);
        Assert.Equal(TestPuzzle.Solution, final.Attempt.Lines[0].Moves);
        Assert.Equal(new MoveAnnotation("Review note", "!"), final.Attempt.Lines[0].Annotations[1]);
    }

    [Fact]
    public void LegacyAttemptIsReadableWithoutSchemaWritesAndCanBeUpgradedOnDesktop()
    {
        var draft = new TrainingSession(TestPuzzle).Snapshot();
        Execute("""
            CREATE TABLE Attempts (Id TEXT PRIMARY KEY, StartedAt TEXT NOT NULL, Payload TEXT NOT NULL);
            INSERT INTO Attempts VALUES ($id, $date, $payload);
            """, command =>
        {
            command.Parameters.AddWithValue("$id", draft.Id.ToString());
            command.Parameters.AddWithValue("$date", draft.StartedAt.ToString("O"));
            command.Parameters.AddWithValue("$payload", JsonSerializer.Serialize(draft));
        });
        var source = new TrainerStore(path, readOnly: true);
        Assert.Equal(draft.Id, Assert.Single(source.History()).Id);
        Assert.Equal(draft.Puzzle, source.ReadAttempt(draft.Id).Puzzle);
        Assert.Equal(0L, Execute("SELECT COUNT(*) FROM pragma_table_info('Attempts') WHERE name = 'Revision'"));

        var upgraded = CreateStore();
        upgraded.Initialize();
        var saved = upgraded.ReadAttemptForEdit(draft.Id);
        Assert.Equal(0L, saved.Revision);
        Assert.Equal(draft.Id, saved.Attempt.Id);
        Assert.Equal(1L, upgraded.SaveAttempt(saved.Attempt with { Mode = SolveMode.Visualization }, saved.Revision));
        Assert.Equal(SolveMode.Visualization, source.ReadAttempt(draft.Id).Mode);
    }

    private TrainerStore CreateStore()
    {
        var store = new TrainerStore(path);
        store.Initialize();
        return store;
    }

    private object? Execute(string sql, Action<SqliteCommand>? bind = null)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        using var connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = path }.ToString());
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        bind?.Invoke(command);
        return command.ExecuteScalar();
    }

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        Directory.Delete(Path.GetDirectoryName(path)!, recursive: true);
    }
}
