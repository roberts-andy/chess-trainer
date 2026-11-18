namespace ChessTrainer.Core;

public static class AttemptEdit
{
    public static TrainingSession Commit(TrainingSession current, Action<TrainingSession> edit, Action<Attempt> save)
    {
        var candidate = new TrainingSession(current.Snapshot());
        candidate.SelectLine(current.ActiveLineIndex);
        candidate.Navigate(current.Cursor);
        edit(candidate);
        save(candidate.Snapshot());
        return candidate;
    }
}
