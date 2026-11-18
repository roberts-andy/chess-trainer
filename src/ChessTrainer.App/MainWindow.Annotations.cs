using ChessTrainer.Core;
using Microsoft.UI.Xaml;
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
    private readonly StackPanel pendingAnnotationActions = new() { Spacing = 6, Visibility = Visibility.Collapsed };

    private void BuildAnnotationEditor(StackPanel analysis)
    {
        foreach (string symbol in new[] { "None", "!", "?", "!!", "??", "!?", "?!" }) moveSymbol.Items.Add(symbol);
        moveSymbol.SelectedIndex = 0;
        // TextChanging is synchronous; a deferred event could otherwise run after another ply is loaded.
        moveComment.TextChanging += (_, _) => SaveAnnotation();
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
        pendingAnnotationActions.Children.Add(Text("This annotation has not been saved.", 12));
        pendingAnnotationActions.Children.Add(Row(
            Action("Retry annotation", SaveAnnotation),
            Action("Discard annotation", () =>
            {
                refreshing = true;
                try { LoadAnnotation(); }
                finally { refreshing = false; }
            })));
        analysis.Children.Add(pendingAnnotationActions);
        analysis.Children.Add(Text("Your notes save automatically, including after submission. Symbols are your assessment, not engine feedback.", 12));
    }

    private void LoadAnnotation()
    {
        moveComment.IsEnabled = moveSymbol.IsEnabled = session is not null && pendingBoardMove is null;
        var annotation = session?.CurrentAnnotation;
        moveComment.Text = annotation?.Comment ?? "";
        moveSymbol.SelectedIndex = Math.Max(0, moveSymbol.Items.IndexOf(string.IsNullOrEmpty(annotation?.Symbol) ? "None" : annotation.Symbol));
        noteLocation.Text = session is null ? "Choose a position to add notes." :
            $"{session.ActiveLine.Name} · {(session.Cursor == 0 ? "Starting position" : $"After ply {session.Cursor}")}";
        pendingAnnotationActions.Visibility = Visibility.Collapsed;
    }

    private MoveAnnotation AnnotationInput
    {
        get
        {
            string symbol = moveSymbol.SelectedIndex <= 0 ? "" : (string)moveSymbol.SelectedItem;
            string comment = moveComment.Text;
            return new(symbol.Length == 0 && string.IsNullOrWhiteSpace(comment) ? "" : comment, symbol);
        }
    }

    private bool HasPendingAnnotation => session is not null && AnnotationInput != session.CurrentAnnotation;

    private void SaveAnnotation() => Guard(() =>
    {
        if (refreshing || pendingBoardMove is not null || !HasPendingAnnotation) return;
        var annotation = AnnotationInput;
        try { CommitEdit(s => s.Annotate(annotation.Comment, annotation.Symbol), clearBoardSelection: false); }
        finally { pendingAnnotationActions.Visibility = HasPendingAnnotation ? Visibility.Visible : Visibility.Collapsed; }
    });

    private string AnnotationMarker(int ply)
    {
        var note = session?.ActiveLine.Annotations.GetValueOrDefault(ply);
        return note is null ? "" : $" {note.Symbol}{(string.IsNullOrWhiteSpace(note.Comment) ? "" : " · note")}";
    }
}
