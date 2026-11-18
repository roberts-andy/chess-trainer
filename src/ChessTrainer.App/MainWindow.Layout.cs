using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Windows.System;

namespace ChessTrainer.App;

public sealed partial class MainWindow
{
    private readonly NavigationView navigation = new()
    {
        PaneDisplayMode = NavigationViewPaneDisplayMode.LeftCompact,
        IsPaneOpen = false, IsSettingsVisible = false,
        IsBackButtonVisible = NavigationViewBackButtonVisible.Collapsed,
        OpenPaneLength = 220, CompactPaneLength = 56, RequestedTheme = ElementTheme.Light
    };
    private readonly Grid practiceToolbar = new() { ColumnSpacing = 20, RowSpacing = 12 };
    private readonly Grid workspace = new() { ColumnSpacing = 20, RowSpacing = 16 };
    private readonly Grid boardArea = new();
    private readonly Border boardFrame = new() { Padding = new Thickness(8), CornerRadius = new CornerRadius(4) };
    private readonly Border boardSurface = Surface();
    private readonly Border analysisSurface = Surface();
    private readonly TextBlock ratingLabel = Text("", 14);
    private readonly TextBlock puzzleIdDetails = Text("", 14);
    private readonly StackPanel resultsSection = new() { Spacing = 12, Visibility = Visibility.Collapsed };
    private readonly ScrollViewer analysisScroll = new()
    {
        HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
        VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
        HorizontalContentAlignment = HorizontalAlignment.Stretch
    };
    private FrameworkElement? toolbarFilters;
    private FrameworkElement? toolbarTitle;
    private PieceSet selectedPieceSet = PieceSet.All[0];
    private bool selectingNavigation;

    private void BuildShell()
    {
        navigation.Background = ThemeSurface;
        navigation.Resources["NavigationViewDefaultPaneBackground"] = ThemeSurface;
        navigation.Resources["NavigationViewExpandedPaneBackground"] = ThemeSurface;
        navigation.Resources["NavigationViewTopPaneBackground"] = ThemeSurface;
        page.HorizontalContentAlignment = HorizontalAlignment.Stretch;
        page.VerticalContentAlignment = VerticalAlignment.Stretch;
        root.Padding = new Thickness(24, 16, 24, 20);
        root.Background = ThemeCanvas;
        root.RequestedTheme = ElementTheme.Light;
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        var header = new Grid { Margin = new Thickness(0, 0, 0, 24), ColumnSpacing = 16 };
        header.ColumnDefinitions.Add(new ColumnDefinition());
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        header.Children.Add(Heading("Chess Trainer", 22));
        libraryCount.VerticalAlignment = VerticalAlignment.Center;
        libraryCount.Foreground = ThemeMuted;
        Grid.SetColumn(libraryCount, 1);
        header.Children.Add(libraryCount);
        root.SizeChanged += (_, _) => libraryCount.Visibility = root.ActualWidth < 640
            ? Visibility.Collapsed : Visibility.Visible;
        root.Children.Add(header);
        Grid.SetRow(notification, 1);
        root.Children.Add(notification);
        notification.Closed += (_, _) => notification.Visibility = Visibility.Collapsed;
        Grid.SetRow(page, 2);
        root.Children.Add(page);
        foreach (var (label, icon) in new[]
        {
            ("Practice", Symbol.Play), ("Results", Symbol.Clock),
            ("Studies", Symbol.Folder), ("Puzzle library", Symbol.Library), ("Appearance", Symbol.Pictures)
        })
            navigation.MenuItems.Add(new NavigationViewItem { Content = label, Icon = new SymbolIcon(icon), Tag = label });
        navigation.SelectionChanged += (_, args) => Guard(() =>
        {
            if (selectingNavigation || args.SelectedItem is not NavigationViewItem item) return;
            switch (item.Tag)
            {
                case "Practice": ShowPractice(); break;
                case "Results": ShowHistory(); break;
                case "Puzzle library": ShowLibrary(); break;
                case "Studies": ShowStudies(); break;
                case "Appearance": ShowAppearance(); break;
            }
        });
        navigation.Content = root;
        Content = navigation;
    }

    private void BuildPractice()
    {
        practice.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        practice.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        practiceToolbar.ColumnDefinitions.Add(new ColumnDefinition());
        practiceToolbar.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        practiceToolbar.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        practiceToolbar.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        puzzleHeading.FontWeight = FontWeights.SemiBold;
        var details = new Button
        {
            Content = new FontIcon { Glyph = "\uE946", FontSize = 14 },
            Style = (Style)Application.Current.Resources["SubtleButtonStyle"],
            Flyout = new Flyout { Content = puzzleIdDetails }, Padding = new Thickness(8)
        };
        AutomationProperties.SetName(details, "Puzzle details");
        ToolTipService.SetToolTip(details, "Rating and Lichess identifier");
        var title = new StackPanel { Spacing = 4 };
        title.Children.Add(puzzleHeading);
        title.Children.Add(Row(ratingLabel, details));
        toolbarTitle = title;
        practiceToolbar.Children.Add(title);
        var filters = Row(minimum, maximum, AsyncAction("Next puzzle", StartPuzzleAsync));
        filters.VerticalAlignment = VerticalAlignment.Bottom;
        foreach (FrameworkElement child in filters.Children) child.VerticalAlignment = VerticalAlignment.Bottom;
        toolbarFilters = filters;
        Grid.SetColumn(filters, 1);
        practiceToolbar.Children.Add(filters);
        practice.Children.Add(practiceToolbar);

        workspace.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1.15, GridUnitType.Star) });
        workspace.ColumnDefinitions.Add(new ColumnDefinition());
        workspace.RowDefinitions.Add(new RowDefinition());
        workspace.RowDefinitions.Add(new RowDefinition { Height = new GridLength(0) });
        Grid.SetRow(workspace, 1);
        practice.Children.Add(workspace);
        BuildBoardSurface();
        BuildAnalysisSurface();
        workspace.Children.Add(boardSurface);
        Grid.SetColumn(analysisSurface, 1);
        workspace.Children.Add(analysisSurface);
        practice.SizeChanged += (_, _) => UpdatePracticeLayout();
        boardArea.SizeChanged += (_, _) => SizeBoard();
        analysisSurface.SizeChanged += (_, args) => moves.Height = Math.Clamp(args.NewSize.Height * 0.18, 80, 144);
        RenderSession();
    }

    private void BuildBoardSurface()
    {
        var panel = new Grid { RowSpacing = 12 };
        panel.RowDefinitions.Add(new RowDefinition());
        panel.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        panel.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        boardFrame.Child = board;
        boardFrame.HorizontalAlignment = HorizontalAlignment.Center;
        boardFrame.VerticalAlignment = VerticalAlignment.Center;
        boardFrame.Background = Brush(231, 234, 229);
        boardArea.Children.Add(boardFrame);
        panel.Children.Add(boardArea);
        var controls = new Grid { ColumnSpacing = 8 };
        controls.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        controls.ColumnDefinitions.Add(new ColumnDefinition());
        controls.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        controls.Children.Add(IconAction(Symbol.Refresh, "Flip board", () => { flipped = !flipped; RenderBoard(); }));
        var playback = Row(
            IconAction(Symbol.Previous, "Starting position", () => Navigate(0)),
            IconAction(Symbol.Back, "Previous move", () => Navigate((session?.Cursor ?? 0) - 1)),
            IconAction(Symbol.Forward, "Next move", () => Navigate((session?.Cursor ?? 0) + 1)),
            IconAction(Symbol.Next, "End of line", () => Navigate(session?.ActiveLine.Moves.Count ?? 0)));
        playback.Spacing = 4;
        playback.HorizontalAlignment = HorizontalAlignment.Center;
        Grid.SetColumn(playback, 1);
        controls.Children.Add(playback);
        var appearance = IconAction(Symbol.Pictures, "Choose chess pieces", ShowAppearance);
        Grid.SetColumn(appearance, 2);
        controls.Children.Add(appearance);
        Grid.SetRow(controls, 1);
        panel.Children.Add(controls);
        turnLabel.FontSize = 13;
        turnLabel.HorizontalAlignment = HorizontalAlignment.Center;
        Grid.SetRow(turnLabel, 2);
        panel.Children.Add(turnLabel);
        boardSurface.Child = panel;
    }

    private void BuildAnalysisSurface()
    {
        var panel = new Grid { RowSpacing = 12 };
        panel.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        panel.RowDefinitions.Add(new RowDefinition());
        panel.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        var heading = new Grid { ColumnSpacing = 8 };
        heading.ColumnDefinitions.Add(new ColumnDefinition());
        heading.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        heading.Children.Add(Heading("Your analysis", 20));
        var notes = Action("Notes", () =>
        {
            moveComment.Focus(FocusState.Programmatic);
            moveComment.StartBringIntoView();
        });
        Grid.SetColumn(notes, 1);
        heading.Children.Add(notes);
        panel.Children.Add(heading);
        var analysis = new StackPanel { Spacing = 10, Margin = new Thickness(0, 0, 8, 0) };
        foreach (string label in new[] { "Move pieces", "Notation + board", "Visualization" }) mode.Items.Add(label);
        mode.SelectedIndex = 0;
        mode.SelectionChanged += (_, _) => Guard(() =>
        {
            if (refreshing || session is not { Submitted: false } current ||
                mode.SelectedIndex < 0 || mode.SelectedIndex == (int)current.Mode) return;
            var nextMode = (Core.SolveMode)mode.SelectedIndex;
            refreshing = true;
            try { mode.SelectedIndex = (int)current.Mode; }
            finally { refreshing = false; }
            EnsureNoPendingPositionEdit();
            CommitEdit(s => s.Mode = nextMode);
            RenderSession();
        });
        analysis.Children.Add(mode);
        modeHint.Foreground = ThemeMuted;
        analysis.Children.Add(modeHint);
        lines.SelectionChanged += (_, _) => Guard(() =>
        {
            if (refreshing || session is null || lines.SelectedIndex < 0 ||
                lines.SelectedIndex == session.ActiveLineIndex) return;
            int nextLine = lines.SelectedIndex;
            if (pendingBoardMove is not null || HasPendingAnnotation)
            {
                refreshing = true;
                try { lines.SelectedIndex = session.ActiveLineIndex; }
                finally { refreshing = false; }
                EnsureNoPendingPositionEdit();
            }
            session.SelectLine(nextLine);
            selectedSquare = null;
            RenderSession();
        });
        analysis.Children.Add(lines);
        moves.SelectionChanged += (_, _) =>
        {
            if (refreshing || session is null || moves.SelectedIndex < 0) return;
            Navigate(moves.SelectedIndex);
        };
        analysis.Children.Add(moves);
        branch.Click += (_, _) => Edit(s => s.BranchOpponentResponse());
        ToolTipService.SetToolTip(branch, "Select one of your moves, then branch before the opponent responds.");
        trim.Click += (_, _) => Edit(s => s.TrimAfterCursor());
        removeVariation.Click += (_, _) => Edit(s => s.RemoveActiveVariation());
        var editActions = new StackPanel { Spacing = 8 };
        editActions.Children.Add(trim);
        editActions.Children.Add(removeVariation);
        var more = new Button { Content = "Line actions", Flyout = new Flyout { Content = editActions } };
        analysis.Children.Add(Row(branch, more));
        pendingBoardMovePanel.Children.Add(pendingBoardMoveLabel);
        pendingBoardMovePanel.Children.Add(Row(
            Action("Retry board move", () => Guard(() =>
            {
                if (pendingBoardMove is { } move) CommitBoardMove(move);
            })),
            Action("Discard board move", () =>
            {
                pendingBoardMove = null;
                selectedSquare = null;
                RenderPendingBoardMove();
                RenderBoard();
            })));
        analysis.Children.Add(pendingBoardMovePanel);
        analysis.Children.Add(notation);
        notation.KeyDown += (_, args) =>
        {
            if (args.Key == VirtualKey.Enter &&
                Microsoft.UI.Input.InputKeyboardSource.GetKeyStateForCurrentThread(VirtualKey.Control)
                    .HasFlag(Windows.UI.Core.CoreVirtualKeyStates.Down))
            {
                AddNotation();
                args.Handled = true;
            }
        };
        append.Click += (_, _) => AddNotation();
        append.HorizontalAlignment = HorizontalAlignment.Right;
        analysis.Children.Add(append);
        var help = Text("SAN or UCI · Include both sides · Ctrl+Enter to add", 12);
        help.Foreground = ThemeMuted;
        analysis.Children.Add(help);
        BuildAnnotationEditor(analysis);
        resultsSection.Children.Add(Heading("Submitted result", 18));
        resultsSection.Children.Add(resultText);
        resultsSection.Children.Add(solutionText);
        analysis.Children.Add(resultsSection);
        analysisScroll.Content = analysis;
        Grid.SetRow(analysisScroll, 1);
        panel.Children.Add(analysisScroll);
        var footer = new StackPanel { Spacing = 8 };
        footer.Children.Add(Text("No evaluation is shown until you submit.", 12));
        submit.Style = (Style)Application.Current.Resources["AccentButtonStyle"];
        submit.MinHeight = 40;
        submit.Click += async (_, _) => await GuardAsync(SubmitAsync);
        footer.Children.Add(submit);
        var footerBorder = new Border
        {
            BorderBrush = ThemeBorder, BorderThickness = new Thickness(0, 1, 0, 0),
            Padding = new Thickness(0, 12, 0, 0), Child = footer
        };
        Grid.SetRow(footerBorder, 2);
        panel.Children.Add(footerBorder);
        analysisSurface.Child = panel;
    }

    private void UpdatePracticeLayout()
    {
        bool stacked = practice.ActualWidth < 780;
        workspace.ColumnSpacing = stacked ? 0 : 20;
        workspace.RowSpacing = stacked ? 16 : 0;
        workspace.ColumnDefinitions[0].Width = new GridLength(stacked ? 1 : 1.15, GridUnitType.Star);
        workspace.ColumnDefinitions[1].Width = new GridLength(stacked ? 0 : 1, GridUnitType.Star);
        workspace.RowDefinitions[0].Height = new GridLength(stacked ? 0.48 : 1, GridUnitType.Star);
        workspace.RowDefinitions[1].Height = new GridLength(stacked ? 0.52 : 0, GridUnitType.Star);
        Grid.SetColumn(analysisSurface, stacked ? 0 : 1);
        Grid.SetRow(analysisSurface, stacked ? 1 : 0);
        bool wrapToolbar = practice.ActualWidth < 1000;
        Grid.SetColumn(toolbarFilters!, wrapToolbar ? 0 : 1);
        Grid.SetRow(toolbarFilters!, wrapToolbar ? 1 : 0);
        Grid.SetColumnSpan(toolbarFilters!, wrapToolbar ? 2 : 1);
        Grid.SetColumnSpan(toolbarTitle!, wrapToolbar ? 2 : 1);
        SizeBoard();
    }

    private void SizeBoard()
    {
        double size = Math.Max(0, Math.Floor(Math.Min(boardArea.ActualWidth, boardArea.ActualHeight) - 18));
        if (Math.Abs(board.Width - size) < 0.5) return;
        board.Width = size;
        board.Height = size;
        boardFrame.Width = size + 16;
        boardFrame.Height = size + 16;
    }

    private void SelectNavigation(UIElement content)
    {
        string label = ReferenceEquals(content, practice) ? "Practice" :
            ReferenceEquals(content, historyPanel) ? "Results" :
            ReferenceEquals(content, libraryPanel) ? "Puzzle library" :
            ReferenceEquals(content, studiesPanel) ? "Studies" : "Appearance";
        selectingNavigation = true;
        try { navigation.SelectedItem = navigation.MenuItems.Cast<NavigationViewItem>().First(i => (string)i.Tag == label); }
        finally { selectingNavigation = false; }
    }

    private static Border Surface() => new()
    {
        Background = ThemeSurface, BorderBrush = ThemeBorder,
        BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(8), Padding = new Thickness(16)
    };

    private static TextBlock Heading(string text, double size)
    {
        var heading = Text(text, size);
        heading.FontWeight = FontWeights.SemiBold;
        return heading;
    }

    private static Button IconAction(Symbol icon, string label, Action action)
    {
        var button = Action("", action);
        button.Content = new SymbolIcon(icon) { Symbol = icon };
        button.Width = 36;
        button.Height = 36;
        button.Padding = new Thickness(8);
        AutomationProperties.SetName(button, label);
        ToolTipService.SetToolTip(button, label);
        return button;
    }
}
