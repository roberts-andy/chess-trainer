namespace ChessTrainer.Core;

public sealed partial class TrainerStore
{
    public Study CreateStudy(string name)
    {
        if (string.IsNullOrWhiteSpace(name)) throw new ArgumentException("Enter a study name.");
        var study = new Study(Guid.NewGuid(), name.Trim());
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = "INSERT INTO Studies(Id,Name) VALUES($id,$name)";
        command.Parameters.AddWithValue("$id", study.Id.ToString());
        command.Parameters.AddWithValue("$name", study.Name);
        command.ExecuteNonQuery();
        return study;
    }

    public void RenameStudy(Guid id, string name)
    {
        if (string.IsNullOrWhiteSpace(name)) throw new ArgumentException("Enter a study name.");
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = "UPDATE Studies SET Name=$name WHERE Id=$id";
        command.Parameters.AddWithValue("$id", id.ToString());
        command.Parameters.AddWithValue("$name", name.Trim());
        if (command.ExecuteNonQuery() != 1) throw new InvalidOperationException("Study not found.");
    }

    public IReadOnlyList<Study> Studies()
    {
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT Id,Name FROM Studies ORDER BY Name,Id";
        using var reader = command.ExecuteReader();
        var studies = new List<Study>();
        while (reader.Read()) studies.Add(new(Guid.Parse(reader.GetString(0)), reader.GetString(1)));
        return studies;
    }

    public IReadOnlyList<StudyPosition> Positions(Guid studyId)
    {
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT Id,StudyId,Title,Fen,Solution,Notes FROM StudyPositions WHERE StudyId=$study ORDER BY rowid";
        command.Parameters.AddWithValue("$study", studyId.ToString());
        using var reader = command.ExecuteReader();
        var positions = new List<StudyPosition>();
        while (reader.Read()) positions.Add(new(Guid.Parse(reader.GetString(0)), Guid.Parse(reader.GetString(1)),
            reader.GetString(2), reader.GetString(3), reader.GetString(4), reader.GetString(5)));
        return positions;
    }

    public void AddPositions(Guid studyId, IReadOnlyList<StudyPosition> positions)
    {
        if (positions.Count == 0) throw new ArgumentException("Choose at least one position.");
        // Validate the complete batch before any persistent changes.
        var validated = positions.Select(p => StudyPosition.Create(studyId, p.Title, p.Fen, p.Solution, p.Notes) with { Id = p.Id }).ToArray();
        using var connection = Open();
        using var transaction = connection.BeginTransaction();
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "SELECT COUNT(*) FROM Studies WHERE Id=$study";
        command.Parameters.AddWithValue("$study", studyId.ToString());
        if ((long)command.ExecuteScalar()! != 1) throw new InvalidOperationException("Choose an existing study.");
        command.CommandText = "INSERT INTO StudyPositions(Id,StudyId,Title,Fen,Solution,Notes) VALUES($id,$study,$title,$fen,$solution,$notes)";
        command.Parameters.AddWithValue("$id", "");
        command.Parameters.AddWithValue("$title", "");
        command.Parameters.AddWithValue("$fen", "");
        command.Parameters.AddWithValue("$solution", "");
        command.Parameters.AddWithValue("$notes", "");
        foreach (var position in validated)
        {
            command.Parameters["$id"].Value = position.Id.ToString();
            command.Parameters["$title"].Value = position.Title;
            command.Parameters["$fen"].Value = position.Fen;
            command.Parameters["$solution"].Value = position.Solution;
            command.Parameters["$notes"].Value = position.Notes;
            command.ExecuteNonQuery();
        }
        transaction.Commit();
    }

    public void UpdatePosition(StudyPosition position)
    {
        var validated = StudyPosition.Create(position.StudyId, position.Title, position.Fen, position.Solution, position.Notes);
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = """
            UPDATE StudyPositions SET Title=$title,Fen=$fen,Solution=$solution,Notes=$notes
            WHERE Id=$id AND StudyId=$study
            """;
        command.Parameters.AddWithValue("$id", position.Id.ToString());
        command.Parameters.AddWithValue("$study", position.StudyId.ToString());
        command.Parameters.AddWithValue("$title", validated.Title);
        command.Parameters.AddWithValue("$fen", validated.Fen);
        command.Parameters.AddWithValue("$solution", validated.Solution);
        command.Parameters.AddWithValue("$notes", validated.Notes);
        if (command.ExecuteNonQuery() != 1) throw new InvalidOperationException("Position not found in the selected study. Select it again before saving.");
    }

    public void MovePosition(Guid id, Guid studyId)
    {
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = """
            UPDATE StudyPositions SET StudyId=$study WHERE Id=$id
            AND EXISTS(SELECT 1 FROM Studies WHERE Id=$study)
            """;
        command.Parameters.AddWithValue("$id", id.ToString());
        command.Parameters.AddWithValue("$study", studyId.ToString());
        if (command.ExecuteNonQuery() != 1) throw new InvalidOperationException("Position or destination study not found.");
    }
}
