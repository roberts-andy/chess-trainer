using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Windows.UI;

namespace ChessTrainer.App;

internal sealed record BoardSet(string Id, string Name, uint Light, uint Dark)
{
    public static IReadOnlyList<BoardSet> All { get; } =
    [
        new("sage", "Sage", 0xEAEDDC, 0x84A089),
        new("walnut", "Walnut", 0xF0D9B5, 0xB58863),
        new("slate", "Slate", 0xDEE3E6, 0x788A96),
        new("sand", "Sand", 0xF4E9D2, 0xC2A678),
        new("ocean", "Ocean", 0xDCEAF0, 0x6395AD),
        new("rose", "Rose", 0xF0E0DC, 0xB5838D)
    ];
}

public sealed partial class MainWindow
{
    private static readonly SolidColorBrush ThemeText = HexBrush(0x232D36);
    private static readonly SolidColorBrush ThemeMuted = HexBrush(0x545E69);
    private static readonly SolidColorBrush ThemeCanvas = HexBrush(0xF3F4F6);
    private static readonly SolidColorBrush ThemeSurface = HexBrush(0xFFFFFF);
    private static readonly SolidColorBrush ThemeBorder = HexBrush(0xE1E4E8);
    private BoardSet selectedBoard = BoardSet.All[0];
    private readonly Grid boardGallery = new() { ColumnSpacing = 16, RowSpacing = 16 };
    private readonly Dictionary<string, (Button Button, TextBlock Status)> boardChoices = [];
    private readonly ComboBox themeChoice = new() { Header = "Application theme", MinWidth = 200 };

    private void LoadAppearance()
    {
        selectedBoard = BoardSet.All.FirstOrDefault(b => b.Id == store.GetSetting("board-set")) ?? BoardSet.All[0];
        ApplyTheme(store.GetSetting("theme") == "dark");
    }

    private void ApplyTheme(bool dark)
    {
        var theme = dark ? ElementTheme.Dark : ElementTheme.Light;
        navigation.RequestedTheme = theme;
        root.RequestedTheme = theme;
        ThemeText.Color = HexColor(dark ? 0xF1F1F1u : 0x232D36u);
        ThemeMuted.Color = HexColor(dark ? 0xBDC3CBu : 0x545E69u);
        ThemeCanvas.Color = HexColor(dark ? 0x202020u : 0xF3F4F6u);
        ThemeSurface.Color = HexColor(dark ? 0x2B2B2Bu : 0xFFFFFFu);
        ThemeBorder.Color = HexColor(dark ? 0x494949u : 0xE1E4E8u);
        if (Microsoft.UI.Windowing.AppWindowTitleBar.IsCustomizationSupported())
        {
            var title = AppWindow.TitleBar;
            title.BackgroundColor = ThemeCanvas.Color;
            title.ForegroundColor = ThemeText.Color;
            title.ButtonBackgroundColor = ThemeCanvas.Color;
            title.ButtonForegroundColor = ThemeText.Color;
            title.InactiveBackgroundColor = ThemeCanvas.Color;
            title.InactiveForegroundColor = ThemeMuted.Color;
            title.ButtonInactiveBackgroundColor = ThemeCanvas.Color;
            title.ButtonInactiveForegroundColor = ThemeMuted.Color;
        }
    }

    private void BuildBoardGallery(StackPanel panel)
    {
        themeChoice.Items.Add("Light");
        themeChoice.Items.Add("Dark");
        themeChoice.SelectedIndex = root.RequestedTheme == ElementTheme.Dark ? 1 : 0;
        themeChoice.SelectionChanged += (_, _) => Guard(() =>
        {
            store.SetSetting("theme", themeChoice.SelectedIndex == 1 ? "dark" : "light");
            ApplyTheme(themeChoice.SelectedIndex == 1);
        });
        panel.Children.Add(themeChoice);
        panel.Children.Add(Heading("Chess boards", 24));
        panel.Children.Add(Text("Choose a board independently of your pieces and application theme.", 14));
        foreach (var set in BoardSet.All)
        {
            var thumbnail = new Grid { Width = 176, Height = 176 };
            for (int i = 0; i < 8; i++)
            {
                thumbnail.RowDefinitions.Add(new RowDefinition());
                thumbnail.ColumnDefinitions.Add(new ColumnDefinition());
            }
            for (int row = 0; row < 8; row++)
            for (int col = 0; col < 8; col++)
            {
                var cell = new Border { Background = HexBrush((row + col) % 2 == 0 ? set.Light : set.Dark) };
                char piece = (row, col) switch { (0, 4) => 'k', (0, 6) => 'n', (7, 4) => 'K', (7, 1) => 'N', _ => ' ' };
                if (piece != ' ') cell.Child = selectedPieceSet.CreateImage(piece, m => Notify(m, InfoBarSeverity.Error));
                Grid.SetRow(cell, row);
                Grid.SetColumn(cell, col);
                thumbnail.Children.Add(cell);
            }
            var content = new StackPanel { Spacing = 10 };
            content.Children.Add(Heading(set.Name, 18));
            content.Children.Add(new Viewbox { Child = thumbnail, MaxHeight = 144, Stretch = Stretch.Uniform });
            var status = Text("", 14);
            content.Children.Add(status);
            var button = new Button
            {
                Content = content, Padding = new Thickness(16), BorderThickness = new Thickness(2),
                HorizontalAlignment = HorizontalAlignment.Stretch, HorizontalContentAlignment = HorizontalAlignment.Stretch
            };
            AutomationProperties.SetName(button, $"Use {set.Name} board");
            button.Click += (_, _) => Guard(() =>
            {
                store.SetSetting("board-set", set.Id);
                selectedBoard = set;
                UpdateBoardSelection();
                RenderBoard();
            });
            boardChoices.Add(set.Id, (button, status));
            boardGallery.Children.Add(button);
        }
        boardGallery.SizeChanged += (_, _) => LayoutGallery(boardGallery, 240);
        panel.Children.Add(boardGallery);
        UpdateBoardSelection();
    }

    private void UpdateBoardSelection()
    {
        foreach (var (id, (button, status)) in boardChoices)
        {
            bool selected = id == selectedBoard.Id;
            status.Text = selected ? "Selected" : "Use this board";
            button.BorderBrush = selected ? (Brush)Application.Current.Resources["SystemControlHighlightAccentBrush"] : ThemeBorder;
            AutomationProperties.SetHelpText(button, selected ? "Currently selected board." : "Select this board.");
        }
    }

    private static void LayoutGallery(Grid gallery, int minimumWidth)
    {
        int columns = Math.Clamp((int)(gallery.ActualWidth / minimumWidth), 1, 3);
        if (gallery.ColumnDefinitions.Count == columns) return;
        gallery.ColumnDefinitions.Clear();
        gallery.RowDefinitions.Clear();
        for (int i = 0; i < columns; i++) gallery.ColumnDefinitions.Add(new ColumnDefinition());
        for (int i = 0; i < (gallery.Children.Count + columns - 1) / columns; i++)
            gallery.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        for (int i = 0; i < gallery.Children.Count; i++)
        {
            var child = (FrameworkElement)gallery.Children[i];
            Grid.SetRow(child, i / columns);
            Grid.SetColumn(child, i % columns);
        }
    }

    private static Color HexColor(uint color) => Color.FromArgb(255, (byte)(color >> 16), (byte)(color >> 8), (byte)color);
    private static SolidColorBrush HexBrush(uint color) => new(HexColor(color));
    private static SolidColorBrush CoordinateBrush(uint color)
    {
        var rgb = HexColor(color);
        static double Linear(byte c) => c / 255.0 <= 0.04045 ? c / 3294.6 : Math.Pow((c / 255.0 + 0.055) / 1.055, 2.4);
        double luminance = 0.2126 * Linear(rgb.R) + 0.7152 * Linear(rgb.G) + 0.0722 * Linear(rgb.B);
        return HexBrush(luminance > 0.179 ? 0x17201Cu : 0xFFFFFFu);
    }
}
