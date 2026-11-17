using System.Text.Json;
using ChessTrainer.Core;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Windows.ApplicationModel.DataTransfer;
using Windows.Graphics.Imaging;
using Windows.Storage.Pickers;
using Windows.Storage.Streams;
using WinRT.Interop;

namespace ChessTrainer.App;

public sealed partial class MainWindow
{
    private Grid? studiesPanel;
    private readonly ScrollViewer studyCollectionScroll = new()
    {
        HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
        VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
        HorizontalContentAlignment = HorizontalAlignment.Stretch
    };
    private readonly ComboBox studyChoice = new() { Header = "Study", MinWidth = 240 };
    private readonly TextBox studyName = new() { Header = "Study name", MinWidth = 240 };
    private readonly ListView studyPositions = new() { Height = 180, SelectionMode = ListViewSelectionMode.Single };
    private readonly TextBlock studySummary = Text("", 14);
    private readonly ComboBox destinationStudy = new() { Header = "Move selected position to", MinWidth = 240 };
    private readonly TextBox positionTitle = new() { Header = "Position title", PlaceholderText = "e.g. Rook endgame" };
    private readonly TextBox positionFen = new()
    {
        Header = "FEN (all six fields)", AcceptsReturn = true, MinHeight = 88,
        TextWrapping = TextWrapping.Wrap, PlaceholderText = "Paste a FEN, or generate one from a screenshot..."
    };
    private readonly TextBox referenceSolution = new() { Header = "Optional reference solution (SAN or UCI; both sides)" };
    private readonly TextBox positionNotes = new() { Header = "Starting-position notes", AcceptsReturn = true, MinHeight = 80, TextWrapping = TextWrapping.Wrap };
    private readonly CheckBox fenReviewed = new() { Content = "I checked the board, side to move, castling and en-passant rights." };
    private readonly Image screenshotPreview = new() { MaxHeight = 260, Stretch = Microsoft.UI.Xaml.Media.Stretch.Uniform };
    private readonly ComboBox imageOrientation = new() { Header = "Screenshot orientation", MinWidth = 200 };
    private readonly TextBox visionEndpoint = new() { Header = "Azure OpenAI / Foundry endpoint" };
    private readonly TextBox visionDeployment = new() { Header = "Vision deployment" };
    private readonly TextBlock visionStatus = Text("", 14);
    private readonly Button recognizeImage = new() { Content = "Generate FEN with Azure AI", IsEnabled = false };
    private readonly Button cancelRecognition = new() { Content = "Cancel recognition", IsEnabled = false };
    private readonly TextBlock positionEditorHeading = Heading("Add a position", 24);
    private readonly Button newPosition = new() { Content = "New position" };
    private readonly Button savePosition = new() { Content = "Save position to selected study" };
    private Guid? editingPositionId;
    private bool refreshingStudies;
    private bool refreshingPositions;
    private byte[]? screenshotPng;
    private CancellationTokenSource? visionCancellation;
    private bool generatedFromImage;

    private void ShowStudies()
    {
        if (!initialized || ImportInProgress()) return;
        if (studiesPanel is null) BuildStudies();
        RefreshStudies((studyChoice.SelectedItem as Study)?.Id);
        ShowPage(studiesPanel!);
    }

    private void BuildStudies()
    {
        studiesPanel = new Grid { RowSpacing = 16 };
        studiesPanel.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        studiesPanel.RowDefinitions.Add(new RowDefinition());
        var header = new StackPanel { Spacing = 8 };
        header.Children.Add(Heading("Studies", 28));
        header.Children.Add(Text("Organize your positions, practice in any of the three modes, and keep notes on every move.", 14));
        studiesPanel.Children.Add(header);
        var workspace = new Grid { ColumnSpacing = 24, RowSpacing = 16 };
        workspace.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(0.8, GridUnitType.Star) });
        workspace.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1.2, GridUnitType.Star) });
        workspace.RowDefinitions.Add(new RowDefinition());
        workspace.RowDefinitions.Add(new RowDefinition { Height = new GridLength(0) });
        var collection = new StackPanel { Spacing = 12 };
        var editor = new StackPanel { Spacing = 12 };
        var collectionScroll = studyCollectionScroll;
        collectionScroll.Content = collection;
        var editorScroll = new ScrollViewer { Content = editor, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalContentAlignment = HorizontalAlignment.Stretch };
        workspace.Children.Add(collectionScroll);
        workspace.Children.Add(editorScroll);
        Grid.SetColumn(editorScroll, 1);
        Grid.SetRow(workspace, 1);
        studiesPanel.Children.Add(workspace);
        studiesPanel.SizeChanged += (_, _) =>
        {
            bool stacked = studiesPanel.ActualWidth < 860;
            workspace.ColumnDefinitions[0].Width = new GridLength(stacked ? 1 : 0.8, GridUnitType.Star);
            workspace.ColumnDefinitions[1].Width = new GridLength(stacked ? 0 : 1.2, GridUnitType.Star);
            workspace.RowDefinitions[0].Height = new GridLength(stacked ? 0.4 : 1, GridUnitType.Star);
            workspace.RowDefinitions[1].Height = new GridLength(stacked ? 0.6 : 0, GridUnitType.Star);
            Grid.SetColumn(editorScroll, stacked ? 0 : 1);
            Grid.SetRow(editorScroll, stacked ? 1 : 0);
        };
        studyChoice.SelectionChanged += (_, _) => Guard(() =>
        {
            if (refreshingStudies) return;
            StartNewPosition();
            RefreshStudyPositions();
        });
        studyPositions.SelectionChanged += (_, _) => Guard(() =>
        {
            if (!refreshingPositions && studyPositions.SelectedItem is StudyPosition position)
                EditStudyPosition(position);
        });
        collection.Children.Add(studyChoice);
        collection.Children.Add(studyName);
        collection.Children.Add(Row(Action("Create study", () => Guard(() =>
        {
            var study = store.CreateStudy(studyName.Text);
            RefreshStudies(study.Id);
            studyName.Text = "";
        })), Action("Rename selected study", () => Guard(() =>
        {
            var study = SelectedStudy();
            store.RenameStudy(study.Id, studyName.Text);
            RefreshStudies(study.Id);
            studyName.Text = "";
        }))));
        collection.Children.Add(studySummary);
        collection.Children.Add(studyPositions);
        collection.Children.Add(Action("Practice selected position", () => Guard(PracticeStudyPosition)));
        collection.Children.Add(destinationStudy);
        collection.Children.Add(Action("Move to study", () => Guard(() =>
        {
            if (studyPositions.SelectedItem is not StudyPosition position || destinationStudy.SelectedItem is not Study target)
                throw new InvalidOperationException("Select a position and a destination study.");
            store.MovePosition(position.Id, target.Id);
            RefreshStudyPositions();
        })));
        newPosition.Click += (_, _) => Guard(StartNewPosition);
        editor.Children.Add(Row(positionEditorHeading, newPosition));
        var sourceChoice = new RadioButtons { Header = "Import source", MaxColumns = 3 };
        foreach (string source in new[] { "Paste FEN", "CSV file", "Screenshot (Azure AI)" }) sourceChoice.Items.Add(source);
        editor.Children.Add(sourceChoice);
        var csv = new StackPanel { Spacing = 12, Visibility = Visibility.Collapsed };
        csv.Children.Add(AsyncAction("Import positions CSV", ImportStudyCsvAsync));
        csv.Children.Add(Text("CSV: FEN is required; Title, Solution and Notes are optional. FEN is the exact starting position, with no setup move. The entire file is validated before any rows are saved.", 12));
        editor.Children.Add(csv);
        var screenshot = new StackPanel { Spacing = 12, Visibility = Visibility.Collapsed };
        screenshot.Children.Add(Text("Generate FEN sends only this image to your Azure resource; inference is billed by Azure. Your Azure CLI sign-in is used. Crop to the board for best results.", 14));
        screenshot.Children.Add(Row(AsyncAction("Choose screenshot", ChooseScreenshotAsync), AsyncAction("Paste screenshot", PasteScreenshotAsync)));
        screenshot.Children.Add(screenshotPreview);
        imageOrientation.Items.Add("White at bottom");
        imageOrientation.Items.Add("Black at bottom");
        imageOrientation.SelectedIndex = 0;
        screenshot.Children.Add(imageOrientation);
        var configPath = Path.Combine(AppContext.BaseDirectory, "Data", "foundry.json");
        using (var config = JsonDocument.Parse(File.ReadAllText(configPath)))
        {
            visionEndpoint.Text = store.GetSetting("vision-endpoint") ?? config.RootElement.GetProperty("endpoint").GetString()!;
            visionDeployment.Text = store.GetSetting("vision-deployment") ?? config.RootElement.GetProperty("deployment").GetString()!;
        }
        var settings = new StackPanel { Spacing = 12 };
        settings.Children.Add(visionEndpoint);
        settings.Children.Add(visionDeployment);
        screenshot.Children.Add(new Expander { Header = "Azure AI connection", Content = settings, HorizontalAlignment = HorizontalAlignment.Stretch });
        recognizeImage.Click += async (_, _) => await GuardAsync(RecognizeScreenshotAsync);
        cancelRecognition.Click += (_, _) => visionCancellation?.Cancel();
        screenshot.Children.Add(Row(recognizeImage, cancelRecognition));
        screenshot.Children.Add(visionStatus);
        screenshot.Children.Add(Text("AI supplies piece placement only. The generated FEN starts with White to move, no castling/en-passant rights and counters 0 1. Correct these fields to match the real position before saving.", 12));
        editor.Children.Add(screenshot);
        sourceChoice.SelectionChanged += (_, _) =>
        {
            csv.Visibility = sourceChoice.SelectedIndex == 1 ? Visibility.Visible : Visibility.Collapsed;
            screenshot.Visibility = sourceChoice.SelectedIndex == 2 ? Visibility.Visible : Visibility.Collapsed;
        };
        sourceChoice.SelectedIndex = 0;
        editor.Children.Add(positionTitle);
        editor.Children.Add(positionFen);
        editor.Children.Add(referenceSolution);
        editor.Children.Add(positionNotes);
        editor.Children.Add(fenReviewed);
        editor.Children.Add(AsyncAction("Preview FEN board", PreviewPositionAsync));
        savePosition.Click += (_, _) => Guard(() =>
        {
            var study = SelectedStudy();
            if (generatedFromImage && fenReviewed.IsChecked != true)
                throw new InvalidOperationException("Preview and confirm the AI-generated FEN, including side to move and hidden state, before saving.");
            var position = StudyPosition.Create(study.Id, positionTitle.Text, positionFen.Text, referenceSolution.Text, positionNotes.Text);
            if (editingPositionId is Guid id)
            {
                position = position with { Id = id };
                store.UpdatePosition(position);
            }
            else store.AddPositions(study.Id, [position]);
            editingPositionId = position.Id;
            RefreshStudyPositions();
            EditStudyPosition(position);
            Notify("Position saved to your study.", InfoBarSeverity.Success);
        });
        editor.Children.Add(savePosition);
    }

    private Study SelectedStudy() => studyChoice.SelectedItem as Study ?? throw new InvalidOperationException("Create or select a study first.");

    private void StartNewPosition()
    {
        studyPositions.SelectedItem = null;
        editingPositionId = null;
        positionTitle.Text = positionFen.Text = referenceSolution.Text = positionNotes.Text = "";
        ResetPositionImage();
        positionEditorHeading.Text = "Add a position";
        savePosition.Content = "Save position to selected study";
    }

    private void EditStudyPosition(StudyPosition position)
    {
        editingPositionId = position.Id;
        positionTitle.Text = position.Title;
        positionFen.Text = position.Fen;
        referenceSolution.Text = position.Solution;
        positionNotes.Text = position.Notes;
        ResetPositionImage();
        positionEditorHeading.Text = "Edit position";
        savePosition.Content = "Save changes";
    }

    private void ResetPositionImage()
    {
        generatedFromImage = false;
        fenReviewed.IsChecked = false;
        screenshotPng = null;
        screenshotPreview.Source = null;
        recognizeImage.IsEnabled = false;
        visionStatus.Text = "";
    }

    private void RefreshStudies(Guid? selected)
    {
        var previous = (studyChoice.SelectedItem as Study)?.Id;
        var studies = store.Studies();
        refreshingStudies = true;
        try
        {
            studyChoice.ItemsSource = studies;
            destinationStudy.ItemsSource = studies;
            studyChoice.SelectedItem = studies.FirstOrDefault(s => s.Id == selected) ?? studies.FirstOrDefault();
            destinationStudy.SelectedItem = studies.FirstOrDefault();
        }
        finally { refreshingStudies = false; }
        if (previous != (studyChoice.SelectedItem as Study)?.Id) StartNewPosition();
        RefreshStudyPositions();
    }

    private void RefreshStudyPositions()
    {
        if (studyChoice.SelectedItem is not Study study)
        {
            studyPositions.ItemsSource = null;
            studySummary.Text = "Create your first study, then add a FEN, CSV, or screenshot.";
            studyPositions.Visibility = Visibility.Collapsed;
            return;
        }
        var positions = store.Positions(study.Id);
        var selected = positions.FirstOrDefault(p => p.Id == editingPositionId);
        refreshingPositions = true;
        try
        {
            studyPositions.ItemsSource = positions;
            studyPositions.SelectedItem = selected;
        }
        finally { refreshingPositions = false; }
        if (editingPositionId is not null && selected is null) StartNewPosition();
        studyPositions.Visibility = positions.Count == 0 ? Visibility.Collapsed : Visibility.Visible;
        studySummary.Text = $"{positions.Count} positions in {study.Name}";
    }

    private void PracticeStudyPosition()
    {
        if (studyPositions.SelectedItem is not StudyPosition position) throw new InvalidOperationException("Select a study position.");
        if (!string.IsNullOrWhiteSpace(notation.Text)) throw new InvalidOperationException("Add or clear pending notation in Practice before changing positions.");
        SaveCurrent();
        var next = new TrainingSession(position.ToPuzzle(), (SolveMode)Math.Max(0, mode.SelectedIndex));
        if (!string.IsNullOrWhiteSpace(position.Notes)) next.Annotate(position.Notes, "");
        store.SaveAttempt(next.Snapshot());
        session = next;
        flipped = !new ChessPosition(next.StartingFen).WhiteToMove;
        selectedSquare = null;
        notification.IsOpen = false;
        ShowPage(practice);
        RenderSession();
    }

    private async Task ImportStudyCsvAsync()
    {
        var study = SelectedStudy();
        var picker = new FileOpenPicker();
        picker.FileTypeFilter.Add(".csv");
        InitializeWithWindow.Initialize(picker, WindowNative.GetWindowHandle(this));
        var file = await picker.PickSingleFileAsync();
        if (file is null) return;
        int count = await Task.Run(() =>
        {
            using var stream = File.OpenRead(file.Path);
            var positions = StudyPosition.ReadCsv(stream, study.Id);
            store.AddPositions(study.Id, positions);
            return positions.Count;
        });
        RefreshStudyPositions();
        Notify($"Imported {count} positions into {study.Name}.", InfoBarSeverity.Success);
    }

    private async Task PreviewPositionAsync()
    {
        string fen = StudyPosition.ValidateFen(positionFen.Text);
        var position = new ChessPosition(fen);
        var preview = new Grid { Width = 320, Height = 320 };
        for (int i = 0; i < 8; i++) { preview.RowDefinitions.Add(new RowDefinition()); preview.ColumnDefinitions.Add(new ColumnDefinition()); }
        for (int row = 0; row < 8; row++)
        for (int col = 0; col < 8; col++)
        {
            char piece = position.PieceAt($"{(char)('a' + col)}{8 - row}");
            var cell = new Border { Background = HexBrush((row + col) % 2 == 0 ? selectedBoard.Light : selectedBoard.Dark) };
            if (piece != ' ') cell.Child = selectedPieceSet.CreateImage(piece, m => Notify(m, InfoBarSeverity.Error));
            Grid.SetRow(cell, row); Grid.SetColumn(cell, col); preview.Children.Add(cell);
        }
        var content = new StackPanel { Spacing = 12 };
        content.Children.Add(preview);
        content.Children.Add(Text($"{(position.WhiteToMove ? "White" : "Black")} to move. Preview is shown with White at the bottom.", 14));
        var dialog = new ContentDialog { XamlRoot = root.XamlRoot, RequestedTheme = root.RequestedTheme,
            Title = "Review position", Content = content, CloseButtonText = "Back to editing" };
        await dialog.ShowAsync();
    }

    private async Task ChooseScreenshotAsync()
    {
        var picker = new FileOpenPicker();
        foreach (string extension in new[] { ".png", ".jpg", ".jpeg", ".bmp", ".webp" }) picker.FileTypeFilter.Add(extension);
        InitializeWithWindow.Initialize(picker, WindowNative.GetWindowHandle(this));
        var file = await picker.PickSingleFileAsync();
        if (file is null) return;
        using var stream = await file.OpenReadAsync();
        await LoadScreenshotAsync(stream);
    }

    private async Task PasteScreenshotAsync()
    {
        var content = Clipboard.GetContent();
        if (!content.Contains(StandardDataFormats.Bitmap)) throw new InvalidOperationException("The clipboard has no image. Copy a screenshot first.");
        using var stream = await (await content.GetBitmapAsync()).OpenReadAsync();
        await LoadScreenshotAsync(stream);
    }

    private async Task LoadScreenshotAsync(IRandomAccessStream stream)
    {
        if (visionCancellation is not null) throw new InvalidOperationException("Finish or cancel recognition before replacing the screenshot.");
        var decoder = await BitmapDecoder.CreateAsync(stream);
        if ((long)decoder.PixelWidth * decoder.PixelHeight > 30_000_000)
            throw new ArgumentException("Crop or resize the screenshot to under 30 megapixels.");
        using var bitmap = await decoder.GetSoftwareBitmapAsync();
        using var output = new InMemoryRandomAccessStream();
        var encoder = await BitmapEncoder.CreateAsync(BitmapEncoder.PngEncoderId, output);
        encoder.SetSoftwareBitmap(bitmap);
        await encoder.FlushAsync();
        if (output.Size > 10 * 1024 * 1024) throw new ArgumentException("Crop or resize the screenshot to under 10 MB.");
        output.Seek(0);
        using (var reader = new DataReader(output.GetInputStreamAt(0)))
        {
            await reader.LoadAsync((uint)output.Size);
            screenshotPng = new byte[(int)output.Size];
            reader.ReadBytes(screenshotPng);
        }
        output.Seek(0);
        var image = new Microsoft.UI.Xaml.Media.Imaging.BitmapImage();
        await image.SetSourceAsync(output);
        screenshotPreview.Source = image;
        recognizeImage.IsEnabled = visionCancellation is null;
        visionStatus.Text = "Screenshot ready. Confirm its orientation, then generate a FEN.";
    }

    private async Task RecognizeScreenshotAsync()
    {
        if (visionCancellation is not null || screenshotPng is null) return;
        if (!string.IsNullOrWhiteSpace(positionFen.Text))
        {
            var confirm = Dialog("Replace the FEN draft?", Text("Recognition will replace the FEN field. Save any position you want to keep first.", 14), "Replace", "Cancel");
            if (await confirm.ShowAsync() != ContentDialogResult.Primary) return;
        }
        using var cancellation = new CancellationTokenSource(TimeSpan.FromMinutes(3));
        visionCancellation = cancellation;
        recognizeImage.IsEnabled = false;
        cancelRecognition.IsEnabled = true;
        positionFen.IsEnabled = imageOrientation.IsEnabled = fenReviewed.IsEnabled = false;
        studyCollectionScroll.IsEnabled = newPosition.IsEnabled = savePosition.IsEnabled = false;
        visionStatus.Text = "Reading the screenshot with Azure AI...";
        try
        {
            string endpoint = visionEndpoint.Text.Trim(), deployment = visionDeployment.Text.Trim();
            using var client = new HttpClient { Timeout = TimeSpan.FromMinutes(3) };
            var result = await new AzureVisionRecognizer(client).RecognizeAsync(screenshotPng, endpoint, deployment,
                imageOrientation.SelectedIndex == 1, cancellation.Token);
            positionFen.Text = $"{result.Placement} w - - 0 1";
            generatedFromImage = true;
            fenReviewed.IsChecked = false;
            store.SetSetting("vision-endpoint", endpoint);
            store.SetSetting("vision-deployment", deployment);
            visionStatus.Text = "FEN generated above. Preview it and verify all six fields before saving." +
                (string.IsNullOrWhiteSpace(result.Uncertainties) ? "" : $"\nAI uncertainty: {result.Uncertainties}");
            positionFen.StartBringIntoView();
        }
        catch (OperationCanceledException)
        {
            visionStatus.Text = "Recognition cancelled or timed out. Your FEN draft was not changed.";
        }
        finally
        {
            visionCancellation = null;
            recognizeImage.IsEnabled = screenshotPng is not null;
            cancelRecognition.IsEnabled = false;
            positionFen.IsEnabled = imageOrientation.IsEnabled = fenReviewed.IsEnabled = true;
            studyCollectionScroll.IsEnabled = newPosition.IsEnabled = savePosition.IsEnabled = true;
        }
    }
}
