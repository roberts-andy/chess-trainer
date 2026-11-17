using ChessTrainer.Core;
using Microsoft.Data.Sqlite;

if (args.Length != 1)
{
    Console.Error.WriteLine("Usage: dotnet run --project tools\\RefreshStarter -- <destination.csv>");
    return 1;
}

string destination = Path.GetFullPath(args[0]);
string temporaryDatabase = destination + ".download.db";
if (File.Exists(temporaryDatabase))
{
    Console.Error.WriteLine($"Temporary database already exists: {temporaryDatabase}. Remove it before retrying.");
    return 1;
}

try
{
    var store = new TrainerStore(temporaryDatabase);
    store.Initialize();
    if (File.Exists(destination)) await store.ImportFileAsync(destination);
    await store.DownloadAsync(new Progress<ImportProgress>(p => Console.WriteLine(p.Message)), limit: 2000);
    using (var connection = new SqliteConnection($"Data Source={temporaryDatabase}"))
    {
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT Id,Fen,Moves,Rating,Themes,GameUrl FROM Puzzles ORDER BY Id";
        using var reader = command.ExecuteReader();
        await using var writer = new StreamWriter(destination + ".tmp");
        await writer.WriteLineAsync("PuzzleId,FEN,Moves,Rating,Themes,GameUrl");
        while (reader.Read())
        {
            string[] fields = Enumerable.Range(0, 6).Select(i =>
                "\"" + reader.GetValue(i).ToString()!.Replace("\"", "\"\"") + "\"").ToArray();
            await writer.WriteLineAsync(string.Join(",", fields));
        }
    }
    File.Move(destination + ".tmp", destination, true);
    Console.WriteLine($"Saved {store.PuzzleCount():N0} CC0 Lichess puzzles to {destination}.");
    return 0;
}
finally
{
    SqliteConnection.ClearAllPools();
    foreach (string suffix in new[] { "", "-wal", "-shm" })
        if (File.Exists(temporaryDatabase + suffix)) File.Delete(temporaryDatabase + suffix);
    if (File.Exists(destination + ".tmp")) File.Delete(destination + ".tmp");
}
