using ChessTrainer.Core;
using Microsoft.UI;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Windows.ApplicationModel.DataTransfer;
using Windows.Storage.Pickers;
using Windows.System;
using Windows.UI;
using WinRT.Interop;

namespace ChessTrainer.App;

public sealed partial class MainWindow : Window
{
    private readonly TrainerStore store;
    private readonly string dataDirectory;
    private readonly Grid root = new();
    private readonly ContentControl page = new();
    private readonly TextBlock libraryCount = Text("", 13);
    private readonly InfoBar notification = new() { IsClosable = true, Visibility = Visibility.Collapsed, Margin = new Thickness(0, 0, 0, 12) };
    private readonly NumberBox minimum = new() { Header = "Min rating", Value = 800, Width = 112, SpinButtonPlacementMode = NumberBoxSpinButtonPlacementMode.Compact };
    private readonly NumberBox maximum = new() { Header = "Max rating", Value = 2000, Width = 112, SpinButtonPlacementMode = NumberBoxSpinButtonPlacementMode.Compact };
    private readonly ComboBox mode = new() { Header = "Solving mode", HorizontalAlignment = HorizontalAlignment.Stretch };
    private readonly ComboBox lines = new() { Header = "Analysis lines", HorizontalAlignment = HorizontalAlignment.Stretch };
    private readonly ListBox moves = new() { Height = 144, SelectionMode = SelectionMode.Single };
    private readonly Grid board = new() { Width = 512, Height = 512 };
    private readonly TextBlock puzzleHeading = Text("Your next challenge", 24);
    private readonly TextBlock turnLabel = Text("Choose a rating range and start a puzzle.", 15);
    private readonly TextBlock modeHint = Text("", 13);
    private readonly TextBlock resultText = Text("", 14);
    private readonly TextBlock solutionText = Text("", 15);
    private readonly TextBox notation = new() { Header = "Enter your line", PlaceholderText = "Nf3 d5 or e2e4 e7e5", AcceptsReturn = true, Height = 72, TextWrapping = TextWrapping.Wrap };
    private readonly Button submit = new() { Content = "Submit answer", HorizontalAlignment = HorizontalAlignment.Stretch };
    private readonly Button append = new() { Content = "Add notation", HorizontalAlignment = HorizontalAlignment.Stretch };
    private readonly Button branch = new() { Content = "Add variation" };
    private readonly Button trim = new() { Content = "Trim after selected position" };
    private readonly Button removeVariation = new() { Content = "Delete variation" };
    private readonly Grid practice = new() { RowSpacing = 20 };
    private readonly ListView history = new() { SelectionMode = ListViewSelectionMode.Single, MinHeight = 200 };
    private readonly TextBlock historySummary = Text("", 14);
    private readonly TextBlock importStatus = Text("", 14);
    private readonly ProgressBar importProgress = new() { IsIndeterminate = true, Visibility = Visibility.Collapsed };
    private readonly Button cancelImport = new() { Content = "Cancel import", IsEnabled = false };
    private readonly List<Button> importButtons = [];
    private readonly Dictionary<string, Button> squares = [];
    private StackPanel? historyPanel;
    private StackPanel? libraryPanel;
    private TrainingSession? session;
    private CancellationTokenSource? importCancellation;
    private string? selectedSquare;
    private bool flipped;
    private bool refreshing;
    private bool initialized;

    public MainWindow()
    {
        Title = "Chess Trainer";
        AppWindow.Resize(new Windows.Graphics.SizeInt32(1240, 940));
        dataDirectory = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ChessTrainer");
        store = new TrainerStore(Path.Combine(dataDirectory, "trainer.db"));
        BuildShell();
        BuildPractice();
        ShowPage(practice);
        root.Loaded += async (_, _) => await GuardAsync(InitializeAsync);
        Closed += (_, _) => { importCancellation?.Cancel(); visionCancellation?.Cancel(); };
    }

    private async Task InitializeAsync()
    {
        if (initialized) return;
        var area = Microsoft.UI.Windowing.DisplayArea.GetFromWindowId(AppWindow.Id,
            Microsoft.UI.Windowing.DisplayAreaFallback.Nearest).WorkArea;
        double scale = root.XamlRoot.RasterizationScale;
        int width = Math.Min((int)(1240 * scale), area.Width);
        int height = Math.Min((int)(860 * scale), area.Height);
        AppWindow.MoveAndResize(new Windows.Graphics.RectInt32(
            area.X + (area.Width - width) / 2, area.Y + (area.Height - height) / 2, width, height));
        await Task.Run(store.Initialize);
        string? savedSet = store.GetSetting("piece-set");
        selectedPieceSet = PieceSet.All.FirstOrDefault(set => set.Id == savedSet) ?? PieceSet.All[0];
        LoadAppearance();
        if (await Task.Run(store.PuzzleCount) == 0)
            await store.ImportFileAsync(Path.Combine(AppContext.BaseDirectory, "Data", "starter-puzzles.csv"));
        initialized = true;
        await UpdateCountAsync();
        var recent = await Task.Run(store.History);
        var draft = recent.FirstOrDefault(a => a.SubmittedAt is null);
        if (draft is not null)
        {
            session = new TrainingSession(await Task.Run(() => store.ReadAttempt(draft.Id)));
            flipped = !new ChessPosition(session.StartingFen).WhiteToMove;
            Notify("Restored your most recent unsubmitted attempt. No solution has been revealed.", InfoBarSeverity.Informational);
            RenderSession();
        }
        else await StartPuzzleAsync();
    }

    private async Task StartPuzzleAsync()
    {
        if (!initialized) return;
        if (!string.IsNullOrWhiteSpace(notation.Text))
        {
            Notify("Add or clear the pending notation before changing puzzles.", InfoBarSeverity.Warning);
            return;
        }
        int min = ReadRating(minimum.Value);
        int max = ReadRating(maximum.Value);
        SaveCurrent();
        var puzzle = await Task.Run(() => store.RandomPuzzle(min, max, session?.Puzzle.Id));
        if (puzzle is null)
        {
            Notify("No imported puzzles match that range. Widen it or import more puzzles in Puzzle library.", InfoBarSeverity.Warning);
            return;
        }
        var next = new TrainingSession(puzzle, (SolveMode)Math.Max(0, mode.SelectedIndex));
        store.SaveAttempt(next.Snapshot());
        session = next;
        flipped = !new ChessPosition(session.StartingFen).WhiteToMove;
        selectedSquare = null;
        notification.IsOpen = false;
        ShowPage(practice);
        RenderSession();
    }

    private static int ReadRating(double value)
    {
        if (double.IsNaN(value) || value != Math.Truncate(value) || value < 0 || value > int.MaxValue)
            throw new ArgumentException("Ratings must be nonnegative whole numbers within the 32-bit integer range.");
        return (int)value;
    }

    private void RenderSession()
    {
        refreshing = true;
        try
        {
            bool editable = session is { Submitted: false };
            mode.IsEnabled = editable;
            notation.IsEnabled = editable;
            append.IsEnabled = editable;
            branch.IsEnabled = editable;
            trim.IsEnabled = editable;
            removeVariation.IsEnabled = editable && session?.ActiveLineIndex > 0;
            submit.IsEnabled = session is not null;
            submit.Content = session?.Submitted == true ? "Submitted and saved" : "Submit answer";
            submit.IsEnabled = editable;
            lines.Items.Clear();
            moves.Items.Clear();
            resultText.Text = "";
            solutionText.Text = "";
            resultsSection.Visibility = Visibility.Collapsed;
            LoadAnnotation();
            if (session is null)
            {
                RenderBoard();
                return;
            }
            mode.SelectedIndex = (int)session.Mode;
            modeHint.Text = session.Mode switch
            {
                SolveMode.Board => "Move both colors. Notation is recorded automatically.",
                SolveMode.Notation => "Enter both sides in notation. The board follows your selected move.",
                _ => "Visualization: the board stays at the puzzle's starting position until submission."
            };
            foreach (var line in session.Lines) lines.Items.Add(line.Name);
            lines.SelectedIndex = session.ActiveLineIndex;
            moves.Items.Add("Starting position" + AnnotationMarker(0));
            var position = new ChessPosition(session.StartingFen);
            int ply = 0;
            foreach (string uci in session.ActiveLine.Moves)
            {
                string prefix = $"{position.MoveNumber}{(position.WhiteToMove ? "." : "...")}";
                string san = position.PlayUci(uci);
                moves.Items.Add($"{prefix}  {san}{AnnotationMarker(++ply)}");
            }
            moves.SelectedIndex = session.Cursor;
            moves.ScrollIntoView(moves.SelectedItem);
            var start = new ChessPosition(session.StartingFen);
            puzzleHeading.Text = session.Puzzle.IsCustom ? session.Puzzle.Title : $"{(start.WhiteToMove ? "White" : "Black")} to solve";
            ratingLabel.Text = session.Puzzle.IsCustom ? "Study position · Your analysis" : $"Puzzle rating {session.Puzzle.Rating:N0}";
            puzzleIdDetails.Text = session.Puzzle.IsCustom
                ? $"Your position\n{(start.WhiteToMove ? "White" : "Black")} to move\n{session.Puzzle.Id}"
                : $"Lichess ID: {session.Puzzle.Id}\nPuzzle rating: {session.Puzzle.Rating:N0}\nSource: Lichess open puzzle database";
            turnLabel.Text =
                (session.Mode == SolveMode.Visualization && !session.Submitted
                    ? $"Starting board frozen · {session.Cursor} plies selected"
                    : $"{(session.CurrentPosition.WhiteToMove ? "White" : "Black")} to move · Play both colors");
            if (session.Submitted)
            {
                resultsSection.Visibility = Visibility.Visible;
                resultText.Text = string.Join("\n\n", session.Results!.Select(r =>
                    $"{r.Name}: {r.Verdict}\n{r.Explanation}"));
                solutionText.Text = session.Puzzle.IsCustom
                    ? session.Puzzle.Solution.Length == 0 ? "No reference solution was supplied." : $"Reference solution: {session.SolutionNotation()}"
                    : $"Lichess solution: {session.SolutionNotation()}\n\nThemes: {session.Puzzle.Themes}";
            }
            RenderBoard();
        }
        finally { refreshing = false; }
    }

    private void RenderBoard()
    {
        board.Children.Clear();
        board.RowDefinitions.Clear();
        board.ColumnDefinitions.Clear();
        squares.Clear();
        for (int i = 0; i < 8; i++)
        {
            board.RowDefinitions.Add(new RowDefinition());
            board.ColumnDefinitions.Add(new ColumnDefinition());
        }
        var position = new ChessPosition(session?.DisplayFen ??
            "8/8/8/8/8/8/8/8 w - - 0 1");
        for (int row = 0; row < 8; row++)
        for (int col = 0; col < 8; col++)
        {
            int file = flipped ? 7 - col : col;
            int rank = flipped ? row + 1 : 8 - row;
            string square = $"{(char)('a' + file)}{rank}";
            char piece = position.PieceAt(square);
            bool light = (file + rank) % 2 == 0;
            var cell = new Grid();
            if (piece != ' ')
                cell.Children.Add(selectedPieceSet.CreateImage(piece, message => Notify(message, InfoBarSeverity.Error)));
            if (col == 0)
                cell.Children.Add(new TextBlock
                {
                    Text = rank.ToString(), FontSize = 10, Margin = new Thickness(3),
                    VerticalAlignment = VerticalAlignment.Top, Foreground = CoordinateBrush(light ? selectedBoard.Light : selectedBoard.Dark),
                    IsHitTestVisible = false
                });
            if (row == 7)
                cell.Children.Add(new TextBlock
                {
                    Text = ((char)('a' + file)).ToString(), FontSize = 10, Margin = new Thickness(3),
                    VerticalAlignment = VerticalAlignment.Bottom, HorizontalAlignment = HorizontalAlignment.Right,
                    Foreground = CoordinateBrush(light ? selectedBoard.Light : selectedBoard.Dark), IsHitTestVisible = false
                });
            var button = new Button
            {
                Content = cell, Padding = new Thickness(0), HorizontalAlignment = HorizontalAlignment.Stretch,
                VerticalAlignment = VerticalAlignment.Stretch, HorizontalContentAlignment = HorizontalAlignment.Stretch,
                VerticalContentAlignment = VerticalAlignment.Stretch, CornerRadius = new CornerRadius(0),
                BorderThickness = new Thickness(selectedSquare == square ? 3 : 0),
                BorderBrush = Brush(205, 143, 38), Background = HexBrush(light ? selectedBoard.Light : selectedBoard.Dark),
                CanDrag = session is { Mode: SolveMode.Board, Submitted: false } && piece != ' ',
                AllowDrop = true
            };
            AutomationProperties.SetName(button, $"{square}: {PieceName(piece)}");
            button.Click += async (_, _) => await GuardAsync(() => ClickSquareAsync(square));
            button.DragStarting += (_, args) =>
            {
                if (session is not { Mode: SolveMode.Board, Submitted: false } ||
                    piece == ' ' || char.IsUpper(piece) != session.CurrentPosition.WhiteToMove)
                {
                    args.Cancel = true;
                    return;
                }
                args.Data.SetText(square);
                args.Data.RequestedOperation = DataPackageOperation.Move;
            };
            button.DragOver += (_, args) =>
            {
                if (session is { Mode: SolveMode.Board, Submitted: false } && args.DataView.Contains(StandardDataFormats.Text))
                    args.AcceptedOperation = DataPackageOperation.Move;
            };
            button.Drop += async (_, args) => await GuardAsync(async () =>
            {
                if (!args.DataView.Contains(StandardDataFormats.Text)) return;
                string from = await args.DataView.GetTextAsync();
                if (squares.ContainsKey(from) && from != square) await MovePieceAsync(from, square);
            });
            Grid.SetRow(button, row);
            Grid.SetColumn(button, col);
            board.Children.Add(button);
            squares[square] = button;
        }
    }

    private async Task ClickSquareAsync(string square)
    {
        if (session is not { Mode: SolveMode.Board, Submitted: false }) return;
        char piece = session.CurrentPosition.PieceAt(square);
        if (piece != ' ' && char.IsUpper(piece) == session.CurrentPosition.WhiteToMove)
        {
            selectedSquare = selectedSquare == square ? null : square;
            RenderBoard();
            return;
        }
        if (selectedSquare is not null)
            await MovePieceAsync(selectedSquare, square);
    }

    private async Task MovePieceAsync(string from, string to)
    {
        if (session is not { Mode: SolveMode.Board, Submitted: false }) return;
        string move = from + to;
        string[] candidates = session.CurrentPosition.LegalMoves().Where(m => m.StartsWith(move, StringComparison.Ordinal)).ToArray();
        if (candidates.Any(m => m.Length == 5))
        {
            var choice = new ComboBox { HorizontalAlignment = HorizontalAlignment.Stretch };
            foreach (string piece in new[] { "Queen", "Rook", "Bishop", "Knight" }) choice.Items.Add(piece);
            choice.SelectedIndex = 0;
            var dialog = Dialog("Promote pawn", choice, "Promote", "Cancel");
            if (await dialog.ShowAsync() != ContentDialogResult.Primary) return;
            move += "qrbn"[choice.SelectedIndex];
        }
        Edit(s => s.AppendUci(move));
    }

    private void AddNotation()
    {
        Guard(() =>
        {
            if (session is null) return;
            session.AppendNotation(notation.Text);
            notation.Text = "";
            selectedSquare = null;
            SaveCurrent();
            RenderSession();
        });
    }

    private void Edit(Action<TrainingSession> action) => Guard(() =>
    {
        if (session is null) return;
        action(session);
        selectedSquare = null;
        SaveCurrent();
        RenderSession();
    });

    private void Navigate(int ply) => Guard(() =>
    {
        session?.Navigate(ply);
        selectedSquare = null;
        RenderSession();
    });

    private async Task SubmitAsync()
    {
        if (session is null || session.Submitted) return;
        if (!string.IsNullOrWhiteSpace(notation.Text))
            throw new InvalidOperationException("Add or clear the pending notation before submitting.");
        var dialog = Dialog("Submit all lines?",
            Text("This locks your moves and saves all lines. A reference solution, if supplied, is revealed. Lines without a reference are Unverified. Comments remain editable during review.", 15),
            "Submit", "Keep thinking");
        if (await dialog.ShowAsync() != ContentDialogResult.Primary) return;
        session.Submit();
        try { SaveCurrent(); }
        catch
        {
            // Keep the solution hidden if persistence fails; the user can retry submission.
            var snapshot = session.Snapshot() with { SubmittedAt = null, Results = null };
            session = new TrainingSession(snapshot);
            throw;
        }
        notification.IsOpen = false;
        RenderSession();
    }

    private void SaveCurrent()
    {
        if (session is not null) store.SaveAttempt(session.Snapshot());
    }

    private void ShowHistory()
    {
        Guard(() =>
        {
            if (!initialized) return;
            if (ImportInProgress()) return;
            SaveCurrent();
            var items = store.History();
            history.ItemsSource = items;
            int submitted = items.Count(a => a.SubmittedAt.HasValue);
            int correct = items.Count(a => a.Outcome == nameof(Verdict.Correct));
            historySummary.Text = $"{items.Count:N0} attempts  |  {submitted:N0} submitted  |  {correct:N0} correct main lines";
            if (historyPanel is not null)
            {
                ShowPage(historyPanel);
                return;
            }
            var panel = new StackPanel { Spacing = 16 };
            panel.Children.Add(Text("Your results", 26));
            panel.Children.Add(historySummary);
            panel.Children.Add(Text("Unsubmitted attempts retain your work without revealing the solution. Select an attempt to review or resume it.", 14));
            panel.Children.Add(history);
            panel.Children.Add(Action("Open / resume selected attempt", () => Guard(() =>
            {
                if (history.SelectedItem is not HistoryItem item) return;
                if (!string.IsNullOrWhiteSpace(notation.Text))
                    throw new InvalidOperationException("Add or clear pending notation in Practice before changing attempts.");
                SaveCurrent();
                session = new TrainingSession(store.ReadAttempt(item.Id));
                flipped = !new ChessPosition(session.StartingFen).WhiteToMove;
                selectedSquare = null;
                notification.IsOpen = false;
                ShowPage(practice);
                RenderSession();
            })));
            historyPanel = panel;
            ShowPage(historyPanel);
        });
    }

    private void ShowLibrary()
    {
        if (!initialized)
        {
            Notify("The local library is still initializing.", InfoBarSeverity.Informational);
            return;
        }
        if (libraryPanel is not null)
        {
            ShowPage(libraryPanel);
            return;
        }
        var panel = new StackPanel { Spacing = 18, MaxWidth = 880, HorizontalAlignment = HorizontalAlignment.Left };
        panel.Children.Add(Text("Puzzle library", 26));
        panel.Children.Add(Text("Lichess open puzzle database", 19));
        panel.Children.Add(Text(TrainerStore.DatabaseUrl, 14));
        panel.Children.Add(Text(
            "The bundled starter set works offline. Download a 10,000-row starter sample, import an existing CSV / .csv.zst, or stream the full database into your local library. Random puzzles are selected uniformly from imported puzzles in your inclusive rating range.\n\n" +
            "The full database contains millions of puzzles. Allow several GB of disk space and time for download and indexing. Imports are atomic: cancelling or failing leaves the existing library intact. The downloaded archive is decompressed as a stream, not loaded into memory.", 15));
        importButtons.Clear();
        var sample = AsyncAction("Download 10,000-puzzle sample", () =>
            RunImportAsync((progress, token) => store.DownloadAsync(progress, token, 10000)));
        var full = AsyncAction("Download full database", async () =>
        {
            var dialog = Dialog("Import the full Lichess database?",
                Text("This downloads the full archive and builds a local index. It may take a long time and use several GB of disk space. You can cancel; existing puzzles are preserved.", 15),
                "Download and import", "Cancel");
            if (await dialog.ShowAsync() == ContentDialogResult.Primary)
                await RunImportAsync((progress, token) => store.DownloadAsync(progress, token));
        });
        var local = AsyncAction("Import local CSV / .zst", async () =>
        {
            var picker = new FileOpenPicker();
            InitializeWithWindow.Initialize(picker, WindowNative.GetWindowHandle(this));
            picker.FileTypeFilter.Add(".csv");
            picker.FileTypeFilter.Add(".zst");
            var file = await picker.PickSingleFileAsync();
            if (file is not null)
                await RunImportAsync((progress, token) => store.ImportFileAsync(file.Path, progress, token));
        });
        importButtons.AddRange([sample, full, local]);
        foreach (var button in importButtons)
        {
            button.IsEnabled = importCancellation is null;
            panel.Children.Add(button);
        }
        panel.Children.Add(importProgress);
        panel.Children.Add(importStatus);
        cancelImport.Click -= CancelImport;
        cancelImport.Click += CancelImport;
        panel.Children.Add(cancelImport);
        panel.Children.Add(Text($"Local data: {dataDirectory}\nNo account, login, telemetry or cloud storage. Network access is only used when you request a puzzle download.", 13));
        panel.Children.Add(Text("Data: Lichess, CC0 1.0. The supplied FEN is advanced by the first setup move before a puzzle is shown. Themes and solutions are hidden until submission.\n\n" +
            "Grading compares your main line to the published solution, accepting checkmate alternatives. The database supplies a single opponent line; other replies are retained as Unverified. This version does not run a chess engine to evaluate those branches.", 13));
        libraryPanel = panel;
        ShowPage(libraryPanel);
    }

    private void CancelImport(object sender, RoutedEventArgs args) => importCancellation?.Cancel();

    private async Task RunImportAsync(Func<IProgress<ImportProgress>, CancellationToken, Task<long>> import)
    {
        if (importCancellation is not null) return;
        importCancellation = new CancellationTokenSource();
        foreach (var button in importButtons) button.IsEnabled = false;
        importProgress.Visibility = Visibility.Visible;
        cancelImport.IsEnabled = true;
        importStatus.Text = "Connecting / reading archive...";
        var progress = new Progress<ImportProgress>(update => importStatus.Text = update.Message);
        try
        {
            long count = await import(progress, importCancellation.Token);
            importStatus.Text = $"Imported {count:N0} records. Existing puzzle IDs were updated, not duplicated.";
            await UpdateCountAsync();
        }
        catch (OperationCanceledException)
        {
            importStatus.Text = "Import cancelled. Existing library unchanged.";
        }
        catch
        {
            importStatus.Text = "Import failed. Existing library unchanged. See the error message above.";
            throw;
        }
        finally
        {
            importCancellation.Dispose();
            importCancellation = null;
            foreach (var button in importButtons) button.IsEnabled = true;
            importProgress.Visibility = Visibility.Collapsed;
            cancelImport.IsEnabled = false;
        }
    }

    private async Task UpdateCountAsync() =>
        libraryCount.Text = $"{await Task.Run(store.PuzzleCount):N0} puzzles · Local library";

    private void ShowPractice()
    {
        if (!ImportInProgress()) ShowPage(practice);
    }

    private bool ImportInProgress()
    {
        if (importCancellation is null) return false;
        Notify("Finish or cancel the import before returning to practice or results.", InfoBarSeverity.Informational);
        return true;
    }

    private void ShowPage(UIElement content)
    {
        if (page.Content is ScrollViewer old) old.Content = null;
        page.Content = null;
        page.Content = ReferenceEquals(content, practice) || ReferenceEquals(content, studiesPanel) ? content : new ScrollViewer
        {
            Content = content, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalContentAlignment = HorizontalAlignment.Stretch
        };
        SelectNavigation(content);
    }

    private ContentDialog Dialog(string title, UIElement content, string primary, string close) => new()
    {
        XamlRoot = root.XamlRoot, RequestedTheme = root.RequestedTheme, Title = title, Content = content,
        PrimaryButtonText = primary, CloseButtonText = close,
        DefaultButton = ContentDialogButton.Primary
    };

    private void Guard(Action action)
    {
        try { action(); }
        catch (Exception ex) { ShowError(ex); }
    }

    private async Task GuardAsync(Func<Task> action)
    {
        try { await action(); }
        catch (Exception ex) { ShowError(ex); }
    }

    private void ShowError(Exception ex)
    {
        Directory.CreateDirectory(dataDirectory);
        File.AppendAllText(Path.Combine(dataDirectory, "errors.log"), $"{DateTimeOffset.Now:O}\n{ex}\n");
        Notify(ex.Message, InfoBarSeverity.Error);
    }

    private void Notify(string message, InfoBarSeverity severity)
    {
        notification.Message = message;
        notification.Severity = severity;
        notification.Visibility = Visibility.Visible;
        notification.IsOpen = true;
    }

    private static Button Action(string label, Action action)
    {
        var button = new Button { Content = label };
        button.Click += (_, _) => action();
        return button;
    }

    private Button AsyncAction(string label, Func<Task> action)
    {
        var button = new Button { Content = label };
        button.Click += async (_, _) =>
        {
            button.IsEnabled = false;
            try { await GuardAsync(action); }
            finally { button.IsEnabled = true; }
        };
        return button;
    }

    private static StackPanel Row(params UIElement[] elements)
    {
        var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 10 };
        foreach (var element in elements) row.Children.Add(element);
        return row;
    }

    private static TextBlock Text(string text, double size) => new()
    {
        Text = text, FontSize = size, TextWrapping = TextWrapping.Wrap,
        Foreground = ThemeText, IsTextSelectionEnabled = true
    };

    private static SolidColorBrush Brush(byte r, byte g, byte b) => new(Color.FromArgb(255, r, g, b));

    private static string PieceName(char piece) => (piece == ' ' ? "" : char.IsUpper(piece) ? "White " : "Black ") +
        (char.ToLowerInvariant(piece) switch
        {
            'k' => "king", 'q' => "queen", 'r' => "rook", 'b' => "bishop", 'n' => "knight", 'p' => "pawn", _ => "empty"
        });
}
