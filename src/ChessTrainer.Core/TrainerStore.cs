using System.Globalization;
using System.Text;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using Microsoft.VisualBasic.FileIO;
using ZstdSharp;

namespace ChessTrainer.Core;

public sealed record ImportProgress(long Rows, string Message);
public sealed record HistoryItem(Guid Id, string PuzzleId, int Rating, DateTimeOffset StartedAt,
    DateTimeOffset? SubmittedAt, SolveMode Mode, string Outcome, string Title = "", bool IsCustom = false)
{
    public override string ToString() =>
        IsCustom ? $"{StartedAt.ToLocalTime():g}   {Title}   Study position   {Mode}   {Outcome}" :
            $"{StartedAt.ToLocalTime():g}   #{PuzzleId}   {Rating}   {Mode}   {Outcome}";
}

public sealed partial class TrainerStore
{
    public const string DatabaseUrl = "https://database.lichess.org/lichess_db_puzzle.csv.zst";
    private readonly string connectionString;

    public TrainerStore(string path)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        connectionString = new SqliteConnectionStringBuilder { DataSource = path, DefaultTimeout = 60 }.ToString();
    }

    private SqliteConnection Open()
    {
        var connection = new SqliteConnection(connectionString);
        connection.Open();
        return connection;
    }

    public void Initialize()
    {
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = """
            PRAGMA journal_mode=WAL;
            CREATE TABLE IF NOT EXISTS Puzzles (
                Id TEXT PRIMARY KEY, Fen TEXT NOT NULL, Moves TEXT NOT NULL,
                Rating INTEGER NOT NULL, Themes TEXT NOT NULL, GameUrl TEXT NOT NULL);
            CREATE INDEX IF NOT EXISTS IX_Puzzles_Rating ON Puzzles(Rating, Id);
            CREATE TABLE IF NOT EXISTS Attempts (
                Id TEXT PRIMARY KEY, StartedAt TEXT NOT NULL, Payload TEXT NOT NULL);
            CREATE TABLE IF NOT EXISTS Settings (
                Name TEXT PRIMARY KEY, Value TEXT NOT NULL);
            CREATE TABLE IF NOT EXISTS Studies (
                Id TEXT PRIMARY KEY, Name TEXT NOT NULL);
            CREATE TABLE IF NOT EXISTS StudyPositions (
                Id TEXT PRIMARY KEY, StudyId TEXT NOT NULL REFERENCES Studies(Id),
                Title TEXT NOT NULL, Fen TEXT NOT NULL, Solution TEXT NOT NULL, Notes TEXT NOT NULL);
            CREATE INDEX IF NOT EXISTS IX_StudyPositions_Study ON StudyPositions(StudyId);
            """;
        command.ExecuteNonQuery();
    }

    public string? GetSetting(string name)
    {
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT Value FROM Settings WHERE Name=$name";
        command.Parameters.AddWithValue("$name", name);
        return (string?)command.ExecuteScalar();
    }

    public void SetSetting(string name, string value)
    {
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO Settings(Name,Value) VALUES($name,$value)
            ON CONFLICT(Name) DO UPDATE SET Value=excluded.Value
            """;
        command.Parameters.AddWithValue("$name", name);
        command.Parameters.AddWithValue("$value", value);
        command.ExecuteNonQuery();
    }

    public long PuzzleCount()
    {
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT COUNT(*) FROM Puzzles";
        return (long)command.ExecuteScalar()!;
    }

    public Puzzle? RandomPuzzle(int minimum, int maximum, string? excludeId = null)
    {
        if (minimum < 0 || maximum < minimum)
            throw new ArgumentException("Enter a nonnegative rating range, with minimum <= maximum.");
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT COUNT(*) FROM Puzzles WHERE Rating BETWEEN $min AND $max AND Id <> $exclude";
        command.Parameters.AddWithValue("$min", minimum);
        command.Parameters.AddWithValue("$max", maximum);
        command.Parameters.AddWithValue("$exclude", excludeId ?? "");
        long count = (long)command.ExecuteScalar()!;
        if (count == 0 && excludeId is not null) return RandomPuzzle(minimum, maximum);
        if (count == 0) return null;
        command.CommandText = """
            SELECT Id,Fen,Moves,Rating,Themes,GameUrl FROM Puzzles
            WHERE Rating BETWEEN $min AND $max AND Id <> $exclude
            ORDER BY Rating,Id LIMIT 1 OFFSET $offset
            """;
        command.Parameters.AddWithValue("$offset", Random.Shared.NextInt64(count));
        using var reader = command.ExecuteReader();
        return reader.Read() ? new(reader.GetString(0), reader.GetString(1), reader.GetString(2),
            reader.GetInt32(3), reader.GetString(4), reader.GetString(5)) : null;
    }

    public void SaveAttempt(Attempt attempt)
    {
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO Attempts(Id,StartedAt,Payload) VALUES($id,$date,$json)
            ON CONFLICT(Id) DO UPDATE SET Payload=excluded.Payload
            """;
        command.Parameters.AddWithValue("$id", attempt.Id.ToString());
        command.Parameters.AddWithValue("$date", attempt.StartedAt.ToString("O"));
        command.Parameters.AddWithValue("$json", JsonSerializer.Serialize(attempt));
        command.ExecuteNonQuery();
    }

    public Attempt ReadAttempt(Guid id)
    {
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT Payload FROM Attempts WHERE Id=$id";
        command.Parameters.AddWithValue("$id", id.ToString());
        return JsonSerializer.Deserialize<Attempt>((string?)command.ExecuteScalar()
            ?? throw new InvalidOperationException("Attempt not found."))!;
    }

    public IReadOnlyList<HistoryItem> History()
    {
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT Payload FROM Attempts ORDER BY StartedAt DESC";
        using var reader = command.ExecuteReader();
        var items = new List<HistoryItem>();
        while (reader.Read())
        {
            var attempt = JsonSerializer.Deserialize<Attempt>(reader.GetString(0))!;
            items.Add(new(attempt.Id, attempt.Puzzle.Id, attempt.Puzzle.Rating, attempt.StartedAt,
                attempt.SubmittedAt, attempt.Mode,
                attempt.Results?.FirstOrDefault()?.Verdict.ToString() ?? "Unsubmitted", attempt.Puzzle.Title, attempt.Puzzle.IsCustom));
        }
        return items;
    }

    public Task<long> ImportFileAsync(string path, IProgress<ImportProgress>? progress = null,
        CancellationToken cancellationToken = default) => Task.Run(() =>
    {
        using var stream = System.IO.File.OpenRead(path);
        return Import(stream, path.EndsWith(".zst", StringComparison.OrdinalIgnoreCase), progress, cancellationToken);
    }, cancellationToken);

    public async Task<long> DownloadAsync(IProgress<ImportProgress>? progress = null,
        CancellationToken cancellationToken = default, int? limit = null)
    {
        using var client = new HttpClient { Timeout = Timeout.InfiniteTimeSpan };
        using var response = await client.GetAsync(DatabaseUrl, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        response.EnsureSuccessStatusCode();
        await using var network = await response.Content.ReadAsStreamAsync(cancellationToken);
        // Disposing the network stream interrupts a pending synchronous decompressor read on cancellation.
        using var registration = cancellationToken.Register(network.Dispose);
        try
        {
            return await Task.Run(() => Import(network, true, progress, cancellationToken, limit), cancellationToken);
        }
        catch (Exception ex) when (cancellationToken.IsCancellationRequested &&
            ex is IOException or ObjectDisposedException or HttpRequestException)
        {
            throw new OperationCanceledException("Import cancelled.", ex, cancellationToken);
        }
    }

    private long Import(Stream source, bool compressed, IProgress<ImportProgress>? progress,
        CancellationToken cancellationToken, int? limit = null)
    {
        using var decompressor = compressed ? new DecompressionStream(source, leaveOpen: true) : null;
        using var parser = new TextFieldParser(decompressor is null ? source : decompressor,
            Encoding.UTF8, true, true) { HasFieldsEnclosedInQuotes = true };
        parser.SetDelimiters(",");
        string[] header = parser.ReadFields() ?? throw new InvalidDataException("The puzzle CSV is empty.");
        string[] required = ["PuzzleId", "FEN", "Moves", "Rating", "Themes", "GameUrl"];
        int[] columns = required.Select(name => Array.IndexOf(header, name)).ToArray();
        if (columns.Contains(-1)) throw new InvalidDataException("The file is not a Lichess puzzle CSV: required columns are missing.");
        using var connection = Open();
        using var transaction = connection.BeginTransaction();
        using var insert = connection.CreateCommand();
        insert.Transaction = transaction;
        insert.CommandText = """
            INSERT INTO Puzzles(Id,Fen,Moves,Rating,Themes,GameUrl) VALUES($id,$fen,$moves,$rating,$themes,$url)
            ON CONFLICT(Id) DO UPDATE SET Fen=excluded.Fen,Moves=excluded.Moves,Rating=excluded.Rating,
                Themes=excluded.Themes,GameUrl=excluded.GameUrl
            """;
        string[] parameters = ["$id", "$fen", "$moves", "$rating", "$themes", "$url"];
        foreach (string name in parameters) insert.Parameters.Add(new SqliteParameter(name, SqliteType.Text));
        insert.Prepare();
        long rows = 0;
        while (!parser.EndOfData && (!limit.HasValue || rows < limit.Value))
        {
            cancellationToken.ThrowIfCancellationRequested();
            string[] fields = parser.ReadFields()!;
            if (fields.Length <= columns.Max())
                throw new InvalidDataException($"Missing columns on CSV record {rows + 2}.");
            string[] values = columns.Select(i => fields[i]).ToArray();
            if (string.IsNullOrWhiteSpace(values[0]) ||
                !int.TryParse(values[3], NumberStyles.Integer, CultureInfo.InvariantCulture, out _) ||
                values[1].Split(' ').Length != 6 || values[2].Split(' ').Length < 2)
                throw new InvalidDataException($"Invalid puzzle data on CSV record {rows + 2}.");
            for (int i = 0; i < parameters.Length; i++) insert.Parameters[i].Value = values[i];
            insert.ExecuteNonQuery();
            rows++;
            if (rows % 1000 == 0) progress?.Report(new(rows, $"Importing {rows:N0} puzzles..."));
        }
        cancellationToken.ThrowIfCancellationRequested();
        transaction.Commit();
        progress?.Report(new(rows, $"Imported {rows:N0} puzzles."));
        return rows;
    }
}
