using System.Text;
using ChessTrainer.Core;
using Xunit;
using ZstdSharp;

namespace ChessTrainer.Tests;

public sealed class StoreTests : IDisposable
{
    private readonly string directory = Path.Combine(Path.GetTempPath(), "ChessTrainer.Tests", Guid.NewGuid().ToString("N"));
    private readonly TrainerStore store;
    private string Starter => Path.Combine(AppContext.BaseDirectory, "Data", "starter-puzzles.csv");

    public StoreTests()
    {
        store = new TrainerStore(Path.Combine(directory, "test.db"));
        store.Initialize();
    }

    [Fact]
    public async Task StarterRowsHaveLegalSolutionsAndImportsAreIdempotent()
    {
        long imported = await store.ImportFileAsync(Starter);
        Assert.True(imported >= 4);
        await store.ImportFileAsync(Starter);
        Assert.Equal(imported, store.PuzzleCount());
        using var parser = new Microsoft.VisualBasic.FileIO.TextFieldParser(Starter);
        parser.SetDelimiters(",");
        parser.ReadFields();
        while (!parser.EndOfData)
        {
            var row = parser.ReadFields()!;
            var position = new ChessPosition(row[1]);
            foreach (string move in row[2].Split(' ')) position.PlayUci(move);
        }
    }

    [Fact]
    public async Task RatingBoundsAreInclusiveAndExcludeImmediateRepeat()
    {
        await store.ImportFileAsync(Starter);
        Assert.Equal(998, store.RandomPuzzle(998, 998)!.Rating);
        Assert.Null(store.RandomPuzzle(0, 1));
        Assert.Null(store.RandomPuzzle(5000, 6000));
        Assert.Throws<ArgumentException>(() => store.RandomPuzzle(2000, 1000));
        var all = new HashSet<string>();
        string? previous = null;
        for (int i = 0; i < 50; i++)
        {
            Puzzle puzzle = store.RandomPuzzle(998, 2671, previous)!;
            Assert.NotEqual(previous, puzzle.Id);
            Assert.InRange(puzzle.Rating, 998, 2671);
            all.Add(puzzle.Id);
            previous = puzzle.Id;
        }
        Assert.True(all.Count > 1);
    }

    [Fact]
    public async Task DraftsAndSubmittedLinesSurviveReopeningDatabase()
    {
        await store.ImportFileAsync(Starter);
        var puzzle = new Puzzle("00sHx", "q3k1nr/1pp1nQpp/3p4/1P2p3/4P3/B1PP1b2/B5PP/5K2 b k - 0 17",
            "e8d7 a2e6 d7d8 f7f8", 1760, "mate mateIn2", "");
        var session = new TrainingSession(puzzle, SolveMode.Visualization);
        session.AppendNotation("Be6+ Kd8 Qf8#");
        session.Navigate(1);
        session.BranchOpponentResponse();
        var draft = session.Snapshot();
        store.SaveAttempt(draft);
        var reopened = new TrainerStore(Path.Combine(directory, "test.db"));
        var restored = new TrainingSession(reopened.ReadAttempt(draft.Id));
        Assert.Null(restored.Results);
        Assert.Equal(restored.StartingFen, restored.DisplayFen);
        Assert.Equal(2, restored.Lines.Count);
        Assert.Equal(SolveMode.Visualization, restored.Mode);
        Assert.Equal("Unsubmitted", Assert.Single(reopened.History()).Outcome);
        restored.SelectLine(0);
        reopened.SaveAttempt(restored.Submit());
        var final = reopened.ReadAttempt(draft.Id);
        Assert.NotNull(final.SubmittedAt);
        Assert.Equal(Verdict.Correct, final.Results![0].Verdict);
        Assert.Equal(Verdict.Incomplete, final.Results[1].Verdict);
        Assert.Equal("Correct", Assert.Single(reopened.History()).Outcome);
        Assert.Equal(3, final.Lines[0].San.Count);
    }

    [Fact]
    public async Task ZstandardCsvImportWorks()
    {
        string compressed = Path.Combine(directory, "puzzles.csv.zst");
        using (var compressor = new Compressor())
            await File.WriteAllBytesAsync(compressed, compressor.Wrap(await File.ReadAllBytesAsync(Starter)).ToArray());
        long count = await store.ImportFileAsync(compressed);
        Assert.Equal(count, store.PuzzleCount());
    }

    [Fact]
    public async Task MalformedAndCancelledImportsDoNotAlterExistingLibrary()
    {
        await store.ImportFileAsync(Starter);
        long original = store.PuzzleCount();
        string malformed = Path.Combine(directory, "bad.csv");
        string[] rows = await File.ReadAllLinesAsync(Starter);
        string changed = "\"newId\"," + rows[1][(rows[1].IndexOf(',') + 1)..];
        await File.WriteAllTextAsync(malformed, $"{rows[0]}\n{changed}\ninvalid,row", Encoding.UTF8);
        await Assert.ThrowsAsync<InvalidDataException>(() => store.ImportFileAsync(malformed));
        Assert.Equal(original, store.PuzzleCount());
        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            store.ImportFileAsync(Starter, cancellationToken: cancelled.Token));
        Assert.Equal(original, store.PuzzleCount());
    }

    [Fact]
    public async Task CancellingAnActiveImportRollsBackAlreadyInsertedRows()
    {
        using var cancellation = new CancellationTokenSource();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => store.ImportFileAsync(Starter,
            new CancellingProgress(cancellation), cancellation.Token));
        Assert.Equal(0, store.PuzzleCount());
    }

    [Fact]
    public void AppearancePreferencePersistsWithoutChangingAttemptsOrPuzzles()
    {
        Assert.Null(store.GetSetting("piece-set"));
        store.SetSetting("piece-set", "chessnut");
        store.SetSetting("piece-set", "spatial");
        var reopened = new TrainerStore(Path.Combine(directory, "test.db"));
        reopened.Initialize();
        Assert.Equal("spatial", reopened.GetSetting("piece-set"));
        Assert.Empty(reopened.History());
        Assert.Equal(0, reopened.PuzzleCount());
    }

    private sealed class CancellingProgress(CancellationTokenSource cancellation) : IProgress<ImportProgress>
    {
        public void Report(ImportProgress value)
        {
            if (value.Rows >= 1000) cancellation.Cancel();
        }
    }

    public void Dispose()
    {
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        Directory.Delete(directory, true);
    }
}
