using System.Data;
using System.Data.Common;
using System.Text.Json;
using Azure.Core;
using ChessTrainer.Core;
using Microsoft.Data.SqlClient;
using Microsoft.Data.Sqlite;

namespace ChessTrainer.Web;

public sealed record SavedAttempt(Attempt Attempt, long Revision);
public sealed class EditConflictException() : InvalidOperationException("This attempt changed on another device. Reload it before continuing.");

public sealed class CloudStore(IConfiguration configuration, TokenCredential credential)
{
    private bool IsSqlite => !string.IsNullOrEmpty(configuration["Storage:SqlitePath"]);

    private async Task<DbConnection> OpenAsync()
    {
        DbConnection connection;
        if (IsSqlite)
            connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = configuration["Storage:SqlitePath"] }.ToString());
        else
        {
            var sql = new SqlConnection(configuration.GetConnectionString("Trainer")
                ?? throw new InvalidOperationException("Configure the Trainer SQL connection string."));
            sql.AccessToken = (await credential.GetTokenAsync(new TokenRequestContext(["https://database.windows.net/.default"]), default)).Token;
            connection = sql;
        }
        try { await connection.OpenAsync(); return connection; }
        catch { await connection.DisposeAsync(); throw; }
    }

    internal static DbCommand Command(DbConnection connection, string sql, params (string Name, object Value)[] parameters)
    {
        var command = connection.CreateCommand();
        command.CommandText = sql;
        foreach (var (name, value) in parameters)
        {
            var parameter = command.CreateParameter();
            parameter.ParameterName = name;
            parameter.Value = value;
            command.Parameters.Add(parameter);
        }
        return command;
    }

    public async Task InitializeAsync()
    {
        await using var connection = await OpenAsync();
        string prefix = IsSqlite ? "CREATE TABLE IF NOT EXISTS " : "CREATE TABLE ";
        string text = IsSqlite ? "TEXT" : "nvarchar(max)";
        string puzzleId = IsSqlite ? "varchar(32)" : "varchar(32) COLLATE Latin1_General_100_BIN2";
        foreach (var (name, definition) in new[]
        {
            ("CloudPuzzles", $"Id {puzzleId} PRIMARY KEY,Fen {text} NOT NULL,Moves {text} NOT NULL,Rating int NOT NULL,Themes {text} NOT NULL,GameUrl {text} NOT NULL"),
            ("CloudStudies", "Id varchar(36) PRIMARY KEY,OwnerId varchar(36) NOT NULL,Name nvarchar(200) NOT NULL"),
            ("CloudPositions", $"Id varchar(36) PRIMARY KEY,StudyId varchar(36) NOT NULL,OwnerId varchar(36) NOT NULL,Title nvarchar(200) NOT NULL,Fen {text} NOT NULL,Solution {text} NOT NULL,Notes {text} NOT NULL"),
            ("CloudAttempts", $"Id varchar(36) PRIMARY KEY,OwnerId varchar(36) NOT NULL,StartedAt varchar(40) NOT NULL,Payload {text} NOT NULL,Revision bigint NOT NULL"),
            ("CloudSettings", $"OwnerId varchar(36) NOT NULL,Name varchar(80) NOT NULL,Value {text} NOT NULL,PRIMARY KEY(OwnerId,Name)")
        })
        {
            string guard = IsSqlite ? "" : $"IF OBJECT_ID('{name}','U') IS NULL ";
            await using var command = Command(connection, $"{guard}{prefix}{name}({definition})");
            await command.ExecuteNonQueryAsync();
        }
        foreach (var (name, table, columns) in new[]
        {
            ("IX_CloudPuzzles_Rating", "CloudPuzzles", "Rating,Id"),
            ("IX_CloudStudies_Owner", "CloudStudies", "OwnerId"),
            ("IX_CloudPositions_Study", "CloudPositions", "OwnerId,StudyId"),
            ("IX_CloudAttempts_Owner", "CloudAttempts", "OwnerId,StartedAt")
        })
        {
            string sql = IsSqlite ? $"CREATE INDEX IF NOT EXISTS {name} ON {table}({columns})"
                : $"IF NOT EXISTS(SELECT 1 FROM sys.indexes WHERE name='{name}') CREATE INDEX {name} ON {table}({columns})";
            await using var command = Command(connection, sql);
            await command.ExecuteNonQueryAsync();
        }
        await InitializePuzzleRatingsAsync(connection);
    }

    private async Task InitializePuzzleRatingsAsync(DbConnection connection)
    {
        await using var transaction = await connection.BeginTransactionAsync();
        await using var exists = Command(connection, IsSqlite
            ? "SELECT name FROM sqlite_master WHERE type='table' AND name='CloudPuzzleRatings'"
            : "SELECT OBJECT_ID('CloudPuzzleRatings','U')");
        exists.Transaction = transaction;
        object? existing = await exists.ExecuteScalarAsync();
        if (existing is not null && existing != DBNull.Value) { await transaction.CommitAsync(); return; }
        await using var initialize = Command(connection, """
            CREATE TABLE CloudPuzzleRatings(Rating int PRIMARY KEY,PuzzleCount bigint NOT NULL CHECK(PuzzleCount>=0));
            INSERT INTO CloudPuzzleRatings(Rating,PuzzleCount)
            SELECT Rating,
            """ + (IsSqlite ? "COUNT(*) FROM CloudPuzzles" : "COUNT_BIG(*) FROM CloudPuzzles WITH(TABLOCKX)") + " GROUP BY Rating;");
        initialize.Transaction = transaction;
        initialize.CommandTimeout = 180;
        await initialize.ExecuteNonQueryAsync();
        string[] triggers = IsSqlite ? [
            """
            CREATE TRIGGER CloudPuzzleInserted AFTER INSERT ON CloudPuzzles BEGIN
                INSERT INTO CloudPuzzleRatings VALUES(new.Rating,1)
                ON CONFLICT(Rating) DO UPDATE SET PuzzleCount=PuzzleCount+1;
            END;
            """,
            """
            CREATE TRIGGER CloudPuzzleUpdated AFTER UPDATE OF Rating ON CloudPuzzles WHEN old.Rating<>new.Rating BEGIN
                UPDATE CloudPuzzleRatings SET PuzzleCount=PuzzleCount-1 WHERE Rating=old.Rating;
                INSERT INTO CloudPuzzleRatings VALUES(new.Rating,1)
                ON CONFLICT(Rating) DO UPDATE SET PuzzleCount=PuzzleCount+1;
            END;
            """,
            """
            CREATE TRIGGER CloudPuzzleDeleted AFTER DELETE ON CloudPuzzles BEGIN
                UPDATE CloudPuzzleRatings SET PuzzleCount=PuzzleCount-1 WHERE Rating=old.Rating;
            END;
            """
        ] : [
            """
            CREATE TRIGGER CloudPuzzleRatingsChanged ON CloudPuzzles AFTER INSERT,UPDATE,DELETE AS
            BEGIN
                SET NOCOUNT ON;
                MERGE CloudPuzzleRatings WITH(HOLDLOCK) AS target
                USING(SELECT Rating,SUM(Delta) AS Delta FROM(
                    SELECT Rating,COUNT_BIG(*) AS Delta FROM inserted GROUP BY Rating
                    UNION ALL SELECT Rating,-COUNT_BIG(*) FROM deleted GROUP BY Rating
                ) AS changes GROUP BY Rating) AS source ON target.Rating=source.Rating
                WHEN MATCHED THEN UPDATE SET PuzzleCount=target.PuzzleCount+source.Delta
                WHEN NOT MATCHED THEN INSERT(Rating,PuzzleCount) VALUES(source.Rating,source.Delta);
            END;
            """
        ];
        foreach (string trigger in triggers)
        {
            await using var command = Command(connection, trigger);
            command.Transaction = transaction;
            await command.ExecuteNonQueryAsync();
        }
        await transaction.CommitAsync();
    }

    public async Task<long> PuzzleCountAsync()
    {
        await using var connection = await OpenAsync();
        await using var command = Command(connection, "SELECT COALESCE(SUM(PuzzleCount),0) FROM CloudPuzzleRatings");
        return Convert.ToInt64(await command.ExecuteScalarAsync());
    }

    public async Task CheckReadyAsync()
    {
        await using var connection = await OpenAsync();
        await using var command = Command(connection,
            "SELECT p.Id FROM CloudPuzzles p JOIN CloudPuzzleRatings r ON p.Rating=r.Rating WHERE 1=0");
        await using var reader = await command.ExecuteReaderAsync();
    }

    public async Task<Puzzle?> RandomPuzzleAsync(int minimum, int maximum, string? exclude)
    {
        if (minimum < 0 || maximum < minimum || maximum > 10000) throw new ArgumentException("Enter a rating range between 0 and 10000.");
        await using var connection = await OpenAsync();
        int? excludedRating = null;
        if (exclude is not null)
        {
            await using var previous = Command(connection, "SELECT Rating FROM CloudPuzzles WHERE Id=@exclude", ("@exclude", exclude));
            object? rating = await previous.ExecuteScalarAsync();
            if (rating is not null && rating != DBNull.Value) excludedRating = Convert.ToInt32(rating);
        }
        var ratings = new List<(int Rating, long Count)>();
        await using (var counts = Command(connection,
            "SELECT Rating,PuzzleCount FROM CloudPuzzleRatings WHERE Rating BETWEEN @min AND @max AND PuzzleCount>0 ORDER BY Rating",
            ("@min", minimum), ("@max", maximum)))
        {
            await using var rows = await counts.ExecuteReaderAsync();
            while (await rows.ReadAsync())
            {
                int rating = rows.GetInt32(0);
                ratings.Add((rating, rows.GetInt64(1) - (rating == excludedRating ? 1 : 0)));
            }
        }
        long total = ratings.Sum(rating => rating.Count);
        if (total == 0) return exclude is null ? null : await RandomPuzzleAsync(minimum, maximum, null);
        long offset = Random.Shared.NextInt64(total);
        int selectedRating = ratings[0].Rating;
        foreach (var rating in ratings)
        {
            if (offset < rating.Count) { selectedRating = rating.Rating; break; }
            offset -= rating.Count;
        }
        await using var command = Command(connection, """
            SELECT Id,Fen,Moves,Rating,Themes,GameUrl FROM CloudPuzzles
            WHERE Rating=@rating AND Id<>@exclude ORDER BY Id
            """ + (IsSqlite ? " LIMIT 1 OFFSET @offset" : " OFFSET @offset ROWS FETCH NEXT 1 ROWS ONLY"),
            ("@rating", selectedRating), ("@exclude", exclude ?? ""), ("@offset", offset));
        await using var reader = await command.ExecuteReaderAsync();
        return await reader.ReadAsync() ? new(reader.GetString(0), reader.GetString(1), reader.GetString(2),
            reader.GetInt32(3), reader.GetString(4), reader.GetString(5)) : null;
    }

    public async Task<IReadOnlyList<Study>> StudiesAsync(string owner)
    {
        await using var connection = await OpenAsync();
        await using var command = Command(connection, "SELECT Id,Name FROM CloudStudies WHERE OwnerId=@owner ORDER BY Name,Id", ("@owner", owner));
        await using var reader = await command.ExecuteReaderAsync();
        var items = new List<Study>();
        while (await reader.ReadAsync()) items.Add(new(Guid.Parse(reader.GetString(0)), reader.GetString(1)));
        return items;
    }

    public async Task SaveStudyAsync(string owner, Study study, bool create)
    {
        if (string.IsNullOrWhiteSpace(study.Name) || study.Name.Trim().Length > 200) throw new ArgumentException("Enter a study name of 1 to 200 characters.");
        await using var connection = await OpenAsync();
        await using var command = Command(connection, create
            ? "INSERT INTO CloudStudies(Id,OwnerId,Name) VALUES(@id,@owner,@name)"
            : "UPDATE CloudStudies SET Name=@name WHERE Id=@id AND OwnerId=@owner",
            ("@id", study.Id.ToString()), ("@owner", owner), ("@name", study.Name.Trim()));
        if (await command.ExecuteNonQueryAsync() != 1) throw new KeyNotFoundException("Study not found.");
    }

    public async Task<IReadOnlyList<StudyPosition>> PositionsAsync(string owner, Guid study)
    {
        await using var connection = await OpenAsync();
        await using var command = Command(connection, "SELECT Id,StudyId,Title,Fen,Solution,Notes FROM CloudPositions WHERE OwnerId=@owner AND StudyId=@study ORDER BY Title,Id",
            ("@owner", owner), ("@study", study.ToString()));
        await using var reader = await command.ExecuteReaderAsync();
        var items = new List<StudyPosition>();
        while (await reader.ReadAsync()) items.Add(new(Guid.Parse(reader.GetString(0)), Guid.Parse(reader.GetString(1)),
            reader.GetString(2), reader.GetString(3), reader.GetString(4), reader.GetString(5)));
        return items;
    }

    public async Task<StudyPosition> PositionAsync(string owner, Guid id)
    {
        await using var connection = await OpenAsync();
        await using var command = Command(connection, "SELECT StudyId,Title,Fen,Solution,Notes FROM CloudPositions WHERE OwnerId=@owner AND Id=@id",
            ("@owner", owner), ("@id", id.ToString()));
        await using var reader = await command.ExecuteReaderAsync();
        if (!await reader.ReadAsync()) throw new KeyNotFoundException("Position not found.");
        return new(id, Guid.Parse(reader.GetString(0)), reader.GetString(1), reader.GetString(2), reader.GetString(3), reader.GetString(4));
    }

    public async Task SavePositionsAsync(string owner, Guid study, IReadOnlyList<StudyPosition> positions, bool create)
    {
        if (positions.Count is 0 or > 10000) throw new ArgumentException("Import between 1 and 10000 positions at a time.");
        var validated = positions.Select(p =>
        {
            if (p.Title.Length > 200 || p.Notes.Length > 50000 || p.Solution.Length > 20000) throw new ArgumentException("Position text is too long.");
            return StudyPosition.Create(study, p.Title, p.Fen, p.Solution, p.Notes) with { Id = p.Id };
        }).ToArray();
        await using var connection = await OpenAsync();
        await using var transaction = await connection.BeginTransactionAsync();
        await using var exists = Command(connection, "SELECT COUNT(*) FROM CloudStudies WHERE Id=@study AND OwnerId=@owner",
            ("@study", study.ToString()), ("@owner", owner));
        exists.Transaction = transaction;
        if (Convert.ToInt64(await exists.ExecuteScalarAsync()) != 1) throw new KeyNotFoundException("Study not found.");
        foreach (var position in validated)
        {
            await using var command = Command(connection, create
                ? "INSERT INTO CloudPositions(Id,StudyId,OwnerId,Title,Fen,Solution,Notes) VALUES(@id,@study,@owner,@title,@fen,@solution,@notes)"
                : "UPDATE CloudPositions SET StudyId=@study,Title=@title,Fen=@fen,Solution=@solution,Notes=@notes WHERE Id=@id AND OwnerId=@owner",
                ("@id", position.Id.ToString()), ("@study", study.ToString()), ("@owner", owner),
                ("@title", position.Title), ("@fen", position.Fen), ("@solution", position.Solution), ("@notes", position.Notes));
            command.Transaction = transaction;
            if (await command.ExecuteNonQueryAsync() != 1) throw new KeyNotFoundException("Position not found.");
        }
        await transaction.CommitAsync();
    }

    public async Task<SavedAttempt> AttemptAsync(string owner, Guid id)
    {
        await using var connection = await OpenAsync();
        await using var command = Command(connection, "SELECT Payload,Revision FROM CloudAttempts WHERE Id=@id AND OwnerId=@owner",
            ("@id", id.ToString()), ("@owner", owner));
        await using var reader = await command.ExecuteReaderAsync();
        if (!await reader.ReadAsync()) throw new KeyNotFoundException("Attempt not found.");
        return new(JsonSerializer.Deserialize<Attempt>(reader.GetString(0))!, reader.GetInt64(1));
    }

    public async Task SaveAttemptAsync(string owner, Attempt attempt, long? revision)
    {
        await using var connection = await OpenAsync();
        await using var command = Command(connection, revision.HasValue
            ? "UPDATE CloudAttempts SET Payload=@payload,Revision=Revision+1 WHERE Id=@id AND OwnerId=@owner AND Revision=@revision"
            : "INSERT INTO CloudAttempts(Id,OwnerId,StartedAt,Payload,Revision) VALUES(@id,@owner,@started,@payload,0)",
            ("@id", attempt.Id.ToString()), ("@owner", owner), ("@started", attempt.StartedAt.ToString("O")),
            ("@payload", JsonSerializer.Serialize(attempt)), ("@revision", revision ?? 0));
        if (await command.ExecuteNonQueryAsync() != 1) throw new EditConflictException();
    }

    public async Task<IReadOnlyList<HistoryItem>> HistoryAsync(string owner, int offset = 0, int limit = 200)
    {
        if (offset < 0 || limit is < 1 or > 200) throw new ArgumentException("Request between 1 and 200 attempts with a nonnegative offset.");
        await using var connection = await OpenAsync();
        await using var command = Command(connection, "SELECT Payload FROM CloudAttempts WHERE OwnerId=@owner ORDER BY StartedAt DESC,Id DESC" +
            (IsSqlite ? " LIMIT @limit OFFSET @offset" : " OFFSET @offset ROWS FETCH NEXT @limit ROWS ONLY"),
            ("@owner", owner), ("@offset", offset), ("@limit", limit));
        await using var reader = await command.ExecuteReaderAsync();
        var items = new List<HistoryItem>();
        while (await reader.ReadAsync())
        {
            var attempt = JsonSerializer.Deserialize<Attempt>(reader.GetString(0))!;
            items.Add(new(attempt.Id, attempt.Puzzle.Id, attempt.Puzzle.Rating, attempt.StartedAt, attempt.SubmittedAt,
                attempt.Mode, attempt.Results?.FirstOrDefault()?.Verdict.ToString() ?? "Unsubmitted", attempt.Puzzle.Title, attempt.Puzzle.IsCustom));
        }
        return items;
    }

    public async Task<string?> SettingAsync(string owner, string name)
    {
        await using var connection = await OpenAsync();
        await using var command = Command(connection, "SELECT Value FROM CloudSettings WHERE OwnerId=@owner AND Name=@name", ("@owner", owner), ("@name", name));
        return (string?)await command.ExecuteScalarAsync();
    }

    public async Task SaveSettingAsync(string owner, string name, string value)
    {
        await using var connection = await OpenAsync();
        await using var command = Command(connection, "UPDATE CloudSettings SET Value=@value WHERE OwnerId=@owner AND Name=@name",
            ("@value", value), ("@owner", owner), ("@name", name));
        if (await command.ExecuteNonQueryAsync() == 0)
        {
            command.CommandText = "INSERT INTO CloudSettings(OwnerId,Name,Value) VALUES(@owner,@name,@value)";
            await command.ExecuteNonQueryAsync();
        }
    }

    public async Task ImportPuzzlesAsync(IReadOnlyList<Puzzle> puzzles)
    {
        if (puzzles.Count == 0) return;
        await using var connection = await OpenAsync();
        await using var transaction = await connection.BeginTransactionAsync();
        if (connection is SqlConnection sql)
        {
            await using var create = Command(connection, "CREATE TABLE #PuzzleImport(Id varchar(32) COLLATE Latin1_General_100_BIN2,Fen nvarchar(max),Moves nvarchar(max),Rating int,Themes nvarchar(max),GameUrl nvarchar(max))");
            create.Transaction = transaction;
            await create.ExecuteNonQueryAsync();
            using var table = new DataTable();
            foreach (string column in new[] { "Id", "Fen", "Moves", "Rating", "Themes", "GameUrl" })
                table.Columns.Add(column, column == "Rating" ? typeof(int) : typeof(string));
            foreach (var puzzle in puzzles) table.Rows.Add(puzzle.Id, puzzle.Fen, puzzle.Moves, puzzle.Rating, puzzle.Themes, puzzle.GameUrl);
            using var bulk = new SqlBulkCopy(sql, SqlBulkCopyOptions.Default, (SqlTransaction)transaction) { DestinationTableName = "#PuzzleImport", BulkCopyTimeout = 180 };
            await bulk.WriteToServerAsync(table);
            await using var merge = Command(connection, """
                UPDATE p SET Fen=s.Fen,Moves=s.Moves,Rating=s.Rating,Themes=s.Themes,GameUrl=s.GameUrl
                FROM CloudPuzzles p JOIN #PuzzleImport s ON p.Id=s.Id;
                INSERT INTO CloudPuzzles(Id,Fen,Moves,Rating,Themes,GameUrl)
                SELECT s.Id,s.Fen,s.Moves,s.Rating,s.Themes,s.GameUrl FROM #PuzzleImport s
                WHERE NOT EXISTS(SELECT 1 FROM CloudPuzzles p WHERE p.Id=s.Id);
                """);
            merge.Transaction = transaction;
            merge.CommandTimeout = 180;
            await merge.ExecuteNonQueryAsync();
        }
        else foreach (var puzzle in puzzles)
        {
            await using var command = Command(connection, """
                INSERT INTO CloudPuzzles(Id,Fen,Moves,Rating,Themes,GameUrl) VALUES(@id,@fen,@moves,@rating,@themes,@url)
                ON CONFLICT(Id) DO UPDATE SET Fen=excluded.Fen,Moves=excluded.Moves,Rating=excluded.Rating,Themes=excluded.Themes,GameUrl=excluded.GameUrl
                """, ("@id", puzzle.Id), ("@fen", puzzle.Fen), ("@moves", puzzle.Moves), ("@rating", puzzle.Rating), ("@themes", puzzle.Themes), ("@url", puzzle.GameUrl));
            command.Transaction = transaction;
            await command.ExecuteNonQueryAsync();
        }
        await transaction.CommitAsync();
    }

    public async Task GrantApplicationAsync(string name, Guid clientId)
    {
        if (IsSqlite || name.Any(c => !char.IsAsciiLetterOrDigit(c) && c != '-')) throw new ArgumentException("Invalid SQL application name.");
        await using var connection = await OpenAsync();
        // Contained application users use the client ID, not the Entra object ID.
        string sid = Convert.ToHexString(clientId.ToByteArray());
        await using var command = Command(connection, $"""
            IF EXISTS(SELECT 1 FROM sys.database_principals WHERE name='{name}' AND sid<>0x{sid})
                DROP USER [{name}];
            IF NOT EXISTS(SELECT 1 FROM sys.database_principals WHERE name='{name}')
                CREATE USER [{name}] WITH SID=0x{sid},TYPE=E;
            GRANT SELECT,INSERT,UPDATE ON SCHEMA::dbo TO [{name}];
            """);
        await command.ExecuteNonQueryAsync();
    }
}
