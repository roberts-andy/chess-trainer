using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;

namespace ChessTrainer.App;

public sealed partial class MainWindow
{
    private StackPanel? appearancePanel;
    private readonly Grid appearanceGallery = new() { ColumnSpacing = 16, RowSpacing = 16 };
    private readonly TextBlock appearanceSelection = Text("", 14);
    private readonly Dictionary<string, (Button Button, TextBlock Status)> pieceSetChoices = [];

    private void ShowAppearance()
    {
        if (!initialized)
        {
            Notify("The local library is still initializing.", InfoBarSeverity.Informational);
            return;
        }
        if (ImportInProgress()) return;
        if (appearancePanel is null) BuildAppearance();
        UpdatePieceSelection();
        ShowPage(appearancePanel!);
    }

    private void BuildAppearance()
    {
        appearancePanel = new StackPanel { Spacing = 16 };
        appearancePanel.Children.Add(Heading("Appearance", 28));
        BuildBoardGallery(appearancePanel);
        appearancePanel.Children.Add(Heading("Chess pieces", 24));
        appearancePanel.Children.Add(Text("Choose the pieces you want to calculate with. Both colors are shown below; your choice applies immediately and is remembered.", 14));
        appearanceSelection.Foreground = ThemeMuted;
        appearancePanel.Children.Add(appearanceSelection);
        foreach (var set in PieceSet.All)
        {
            var content = new StackPanel { Spacing = 12 };
            content.Children.Add(Heading(set.Name, 20));
            content.Children.Add(Text(set.Description, 14));
            var preview = new Grid { Width = 300, Height = 100 };
            for (int i = 0; i < 6; i++) preview.ColumnDefinitions.Add(new ColumnDefinition());
            preview.RowDefinitions.Add(new RowDefinition());
            preview.RowDefinitions.Add(new RowDefinition());
            for (int row = 0; row < 2; row++)
            for (int col = 0; col < 6; col++)
            {
                char piece = (row == 0 ? "KQRBNP" : "kqrbnp")[col];
                var cell = new Border
                {
                    Background = (col + row) % 2 == 0 ? Brush(234, 237, 220) : Brush(132, 160, 137),
                    Child = set.CreateImage(piece, message => Notify(message, InfoBarSeverity.Error))
                };
                Grid.SetColumn(cell, col);
                Grid.SetRow(cell, row);
                preview.Children.Add(cell);
            }
            content.Children.Add(new Viewbox
            {
                Child = preview, Stretch = Microsoft.UI.Xaml.Media.Stretch.Uniform,
                MaxHeight = 120, HorizontalAlignment = HorizontalAlignment.Stretch
            });
            var credit = Text(set.Credit, 12);
            credit.Foreground = ThemeMuted;
            content.Children.Add(credit);
            var status = Text("", 14);
            status.FontWeight = FontWeights.SemiBold;
            content.Children.Add(status);
            var choice = new Button
            {
                Content = content, HorizontalAlignment = HorizontalAlignment.Stretch,
                HorizontalContentAlignment = HorizontalAlignment.Stretch, VerticalContentAlignment = VerticalAlignment.Top,
                Padding = new Thickness(20), CornerRadius = new CornerRadius(8), BorderThickness = new Thickness(2)
            };
            AutomationProperties.SetName(choice, $"Use {set.Name} chess pieces");
            choice.Click += (_, _) => Guard(() =>
            {
                store.SetSetting("piece-set", set.Id);
                selectedPieceSet = set;
                UpdatePieceSelection();
                RenderBoard();
            });
            pieceSetChoices.Add(set.Id, (choice, status));
            appearanceGallery.Children.Add(choice);
        }
        appearanceGallery.SizeChanged += (_, _) => LayoutPieceGallery();
        appearancePanel.Children.Add(appearanceGallery);
        appearancePanel.Children.Add(Text("All artwork is bundled for offline use. Artist credits and licenses are included with the app in Assets\\Pieces.", 12));
        var guide = new HyperlinkButton
        {
            Content = "Design foundation: Microsoft Fluent 2",
            NavigateUri = new Uri("https://fluent2.microsoft.design/components/windows")
        };
        appearancePanel.Children.Add(guide);
    }

    private void LayoutPieceGallery()
    {
        int columns = Math.Clamp((int)(appearanceGallery.ActualWidth / 340), 1, 3);
        if (appearanceGallery.ColumnDefinitions.Count == columns) return;
        appearanceGallery.ColumnDefinitions.Clear();
        appearanceGallery.RowDefinitions.Clear();
        for (int i = 0; i < columns; i++) appearanceGallery.ColumnDefinitions.Add(new ColumnDefinition());
        int rows = (PieceSet.All.Count + columns - 1) / columns;
        for (int i = 0; i < rows; i++) appearanceGallery.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        for (int i = 0; i < appearanceGallery.Children.Count; i++)
        {
            var choice = (Button)appearanceGallery.Children[i];
            Grid.SetColumn(choice, i % columns);
            Grid.SetRow(choice, i / columns);
        }
    }

    private void UpdatePieceSelection()
    {
        appearanceSelection.Text = $"Current set: {selectedPieceSet.Name}";
        foreach (var (id, (button, status)) in pieceSetChoices)
        {
            bool selected = id == selectedPieceSet.Id;
            status.Text = selected ? "Selected" : "Use this set";
            button.BorderBrush = selected
                ? (Microsoft.UI.Xaml.Media.Brush)Application.Current.Resources["SystemControlHighlightAccentBrush"]
                : ThemeBorder;
            AutomationProperties.SetHelpText(button, selected ? "Currently selected piece set." : "Select this piece set.");
        }
    }
}
