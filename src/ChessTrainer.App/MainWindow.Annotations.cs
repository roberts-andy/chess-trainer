using Microsoft.UI.Xaml.Controls;

namespace ChessTrainer.App;

public sealed partial class MainWindow
{
    private readonly TextBox moveComment = new()
    {
        Header = "Comment on selected position", AcceptsReturn = true, MinHeight = 96,
        TextWrapping = Microsoft.UI.Xaml.TextWrapping.Wrap, PlaceholderText = "Plans, candidate moves, or review notes..."
    };
    private readonly ComboBox moveSymbol = new() { Header = "Your annotation", MinWidth = 160 };
    private readonly TextBlock noteLocation = Text("", 12);

    private void BuildAnnotationEditor(StackPanel analysis)
    {
        foreach (string symbol in new[] { "None", "!", "?", "!!", "??", "!?", "?!" }) moveSymbol.Items.Add(symbol);
        moveSymbol.SelectedIndex = 0;
        moveComment.TextChanged += (_, _) => SaveAnnotation();
        moveSymbol.SelectionChanged += (_, _) => SaveAnnotation();
        analysis.Children.Add(Heading("Comments & annotations", 18));
        analysis.Children.Add(Action("Back to moves", () =>
        {
            mode.Focus(Microsoft.UI.Xaml.FocusState.Programmatic);
            mode.StartBringIntoView();
        }));
        analysis.Children.Add(noteLocation);
        analysis.Children.Add(moveSymbol);
        analysis.Children.Add(moveComment);
        analysis.Children.Add(Text("Your notes save automatically, including after submission. Symbols are your assessment, not engine feedback.", 12));
    }

    private void LoadAnnotation()
    {
        moveComment.IsEnabled = moveSymbol.IsEnabled = session is not null;
        var annotation = session?.CurrentAnnotation;
        moveComment.Text = annotation?.Comment ?? "";
        moveSymbol.SelectedIndex = Math.Max(0, moveSymbol.Items.IndexOf(string.IsNullOrEmpty(annotation?.Symbol) ? "None" : annotation.Symbol));
        noteLocation.Text = session is null ? "Choose a position to add notes." :
            $"{session.ActiveLine.Name} · {(session.Cursor == 0 ? "Starting position" : $"After ply {session.Cursor}")}";
    }

    private void SaveAnnotation() => Guard(() =>
    {
        if (refreshing || session is null) return;
        session.Annotate(moveComment.Text, moveSymbol.SelectedIndex <= 0 ? "" : (string)moveSymbol.SelectedItem);
        SaveCurrent();
    });

    private string AnnotationMarker(int ply)
    {
        var note = session?.ActiveLine.Annotations.GetValueOrDefault(ply);
        return note is null ? "" : $" {note.Symbol}{(string.IsNullOrWhiteSpace(note.Comment) ? "" : " · note")}";
    }
}
