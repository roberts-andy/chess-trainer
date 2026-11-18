using ChessTrainer.Core;
using Microsoft.Data.Sqlite;

namespace ChessTrainer.Web;

public static class LocalMigration
{
    public static long CreateSnapshot(string sourcePath, string destinationPath)
    {
        if (!File.Exists(sourcePath)) throw new FileNotFoundException("Local trainer database not found.", sourcePath);
        if (File.Exists(destinationPath)) throw new IOException("Choose a new snapshot file.");
        using var source = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = sourcePath, Mode = SqliteOpenMode.ReadOnly, Pooling = false
        }.ToString());
        using var destination = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = destinationPath, Pooling = false
        }.ToString());
        source.Open();
        destination.Open();
        source.BackupDatabase(destination);
        using var portable = destination.CreateCommand();
        portable.CommandText = "PRAGMA journal_mode=DELETE";
        if (!string.Equals(portable.ExecuteScalar() as string, "delete", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("The snapshot could not be made portable for a read-only container.");
        using var count = destination.CreateCommand();
        count.CommandText = "SELECT COUNT(*) FROM Puzzles";
        return (long)count.ExecuteScalar()!;
    }

    public static async Task ImportAsync(CloudStore cloud, string owner, string path, int? puzzleLimit = null)
    {
        if (!File.Exists(path)) throw new FileNotFoundException("Local trainer database not found.", path);
        await using var connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = path, Mode = SqliteOpenMode.ReadOnly }.ToString());
        await connection.OpenAsync();
        await using (var command = connection.CreateCommand())
        {
            command.CommandText = "SELECT Id,Fen,Moves,Rating,Themes,GameUrl FROM Puzzles" + (puzzleLimit.HasValue ? " LIMIT @limit" : "");
            if (puzzleLimit.HasValue) command.Parameters.AddWithValue("@limit", puzzleLimit.Value);
            await using var reader = await command.ExecuteReaderAsync();
            var batch = new List<Puzzle>();
            long copied = 0;
            while (await reader.ReadAsync())
            {
                batch.Add(new(reader.GetString(0), reader.GetString(1), reader.GetString(2), reader.GetInt32(3), reader.GetString(4), reader.GetString(5)));
                if (batch.Count == 50000)
                {
                    await cloud.ImportPuzzlesAsync(batch);
                    copied += batch.Count;
                    Console.WriteLine($"Copied {copied:N0} puzzles.");
                    batch.Clear();
                }
            }
            if (batch.Count > 0)
            {
                await cloud.ImportPuzzlesAsync(batch);
                Console.WriteLine($"Copied {copied + batch.Count:N0} puzzles.");
            }
        }
        var local = new TrainerStore(path, readOnly: true);
        var studies = await cloud.StudiesAsync(owner);
        foreach (var study in local.Studies())
        {
            if (!studies.Any(s => s.Id == study.Id)) await cloud.SaveStudyAsync(owner, study, true);
            var existing = await cloud.PositionsAsync(owner, study.Id);
            var missing = local.Positions(study.Id).Where(p => existing.All(e => e.Id != p.Id)).ToArray();
            if (missing.Length > 0) await cloud.SavePositionsAsync(owner, study.Id, missing, true);
        }
        foreach (var history in local.History())
        {
            try { await cloud.AttemptAsync(owner, history.Id); }
            catch (KeyNotFoundException) { await cloud.SaveAttemptAsync(owner, local.ReadAttempt(history.Id), null); }
        }
        foreach (string name in new[] { "theme", "board-set", "piece-set" })
            if (await cloud.SettingAsync(owner, name) is null && local.GetSetting(name) is string value)
                await cloud.SaveSettingAsync(owner, name, value);
    }
}
