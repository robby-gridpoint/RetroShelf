using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Text.Json;
using System.Windows;
using System.Windows.Input;
using System.Windows.Data;
using Microsoft.Win32;
using RetroShelf.Models;
using RetroShelf.Services;
using DataFormats = System.Windows.DataFormats;
using DragDropEffects = System.Windows.DragDropEffects;
using DragEventArgs = System.Windows.DragEventArgs;
using IDataObject = System.Windows.IDataObject;
using KeyEventArgs = System.Windows.Input.KeyEventArgs;
using MessageBox = System.Windows.MessageBox;
using OpenFileDialog = Microsoft.Win32.OpenFileDialog;

namespace RetroShelf.Views;

public partial class MainWindow : Window
{
    private readonly LibraryService libraryService = new();
    private readonly CompatibilityService compatibilityService = new();
    private readonly AppSettingsService settingsService = new();
    private readonly TrayIconService trayIconService = new();
    private readonly AppSettings appSettings;
    private readonly ObservableCollection<GameEntry> games;
    private readonly ICollectionView gamesView;
    private IReadOnlyDictionary<string, CompatibilityInfo> compatibilityEntries =
        new Dictionary<string, CompatibilityInfo>(StringComparer.OrdinalIgnoreCase);
    private CancellationTokenSource? installCancellation;
    private bool isRefreshingCompatibility;
    private bool isUpdatingDetails;
    private bool closeAfterInstall;
    private bool isGameSessionActive;

    public MainWindow()
    {
        InitializeComponent();

        appSettings = settingsService.Load();
        ShowPostLaunchSummariesMenuItem.IsChecked = appSettings.ShowPostLaunchSummaries;
        trayIconService.RestoreRequested += TrayIconService_RestoreRequested;

        games = new ObservableCollection<GameEntry>(libraryService.Load());
        gamesView = CollectionViewSource.GetDefaultView(games);
        gamesView.Filter = FilterGame;
        gamesView.SortDescriptions.Add(new SortDescription(nameof(GameEntry.IsFavorite), ListSortDirection.Descending));
        gamesView.SortDescriptions.Add(new SortDescription(nameof(GameEntry.Name), ListSortDirection.Ascending));
        GameList.ItemsSource = gamesView;

        UpdateLibraryState();
        GameList.SelectedItem = gamesView.Cast<GameEntry>().FirstOrDefault();
    }

    private GameEntry? SelectedGame => GameList.SelectedItem as GameEntry;

    private async void Window_Loaded(object sender, RoutedEventArgs e)
    {
        await RefreshCompatibilityAsync();
    }

    private async void RefreshCompatibility_Click(object sender, RoutedEventArgs e)
    {
        await RefreshCompatibilityAsync();
    }

    private async Task RefreshCompatibilityAsync()
    {
        if (isRefreshingCompatibility)
        {
            return;
        }

        isRefreshingCompatibility = true;
        StatusText.Text = "Checking compatibility...";

        try
        {
            compatibilityEntries = await compatibilityService.LoadAsync();
            int matchCount = 0;
            foreach (GameEntry game in games)
            {
                ApplyCompatibility(game);
                if (game.HasCompatibility)
                {
                    matchCount++;
                }
            }

            StatusText.Text = matchCount == 1
                ? "Compatibility updated: 1 game matched"
                : $"Compatibility updated: {matchCount} games matched";
        }
        catch (Exception exception) when (exception is HttpRequestException or JsonException or IOException or TaskCanceledException)
        {
            compatibilityEntries = new Dictionary<string, CompatibilityInfo>(StringComparer.OrdinalIgnoreCase);
            foreach (GameEntry game in games)
            {
                game.Compatibility = null;
            }

            StatusText.Text = "Compatibility feed unavailable; games marked Unknown";
        }
        finally
        {
            isRefreshingCompatibility = false;
            gamesView.Refresh();
            ShowSelectedGame();
        }
    }

    private void CloseButton_Click(object sender, RoutedEventArgs e)
    {
        Close();
    }

    private void AboutButton_Click(object sender, RoutedEventArgs e)
    {
        AboutWindow dialog = new() { Owner = this };
        dialog.ShowDialog();
    }

    private void ShowPostLaunchSummaries_Click(object sender, RoutedEventArgs e)
    {
        appSettings.ShowPostLaunchSummaries = ShowPostLaunchSummariesMenuItem.IsChecked;
        settingsService.Save(appSettings);
    }

    private void Window_Closing(object? sender, CancelEventArgs e)
    {
        if (isGameSessionActive)
        {
            e.Cancel = true;
            MinimizeToTray();
            return;
        }

        if (installCancellation is null)
        {
            trayIconService.Dispose();
            return;
        }

        e.Cancel = true;
        closeAfterInstall = true;
        CancelCurrentInstall();
    }

    private void InstallButton_Click(object sender, RoutedEventArgs e)
    {
        OpenFileDialog dialog = new()
        {
            Title = "Install an XBLIG or indie game",
            Filter = "ZIP archives (*.zip)|*.zip",
            CheckFileExists = true,
            Multiselect = false
        };

        if (dialog.ShowDialog(this) == true)
        {
            _ = InstallArchiveAsync(dialog.FileName);
        }
    }

    private void OpenGamesDirectory_Click(object sender, RoutedEventArgs e)
    {
        string gamesDirectory = libraryService.gamesDirectory;
        if (!Directory.Exists(gamesDirectory))
        {
            Directory.CreateDirectory(gamesDirectory);
        }
        Process.Start(new ProcessStartInfo
        {
            FileName = "explorer.exe",
            Arguments = $"\"{gamesDirectory}\"",
            UseShellExecute = true
        });
    }

    private async Task InstallArchiveAsync(string archivePath)
    {
        if (installCancellation is not null)
        {
            return;
        }

        string gameName = Path.GetFileNameWithoutExtension(archivePath);
        installCancellation = new CancellationTokenSource();
        CancellationTokenSource currentCancellation = installCancellation;
        Progress<InstallProgress> progress = new(UpdateInstallProgress);

        InstallNameText.Text = gameName;
        InstallStageText.Text = "Preparing installation";
        InstallDetailText.Text = Path.GetFileName(archivePath);
        InstallPercentText.Text = "0%";
        InstallProgressBar.Value = 0;
        CancelInstallButton.IsEnabled = true;
        InstallOverlay.Visibility = Visibility.Visible;
        StatusText.Text = $"Installing {gameName}...";

        try
        {
            GameEntry game = await libraryService.InstallAsync(archivePath, progress, currentCancellation.Token);
            ApplyCompatibility(game);
            games.Add(game);
            SaveLibrary();
            gamesView.Refresh();
            GameList.SelectedItem = game;
            StatusText.Text = $"Installed {game.Name}";
        }
        catch (OperationCanceledException)
        {
            StatusText.Text = "Installation canceled";
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or InvalidDataException)
        {
            MessageBox.Show(this, exception.Message, "Installation failed", MessageBoxButton.OK, MessageBoxImage.Error);
            StatusText.Text = "Installation failed";
        }
        finally
        {
            InstallOverlay.Visibility = Visibility.Collapsed;
            currentCancellation.Dispose();
            installCancellation = null;
            UpdateLibraryState();

            if (closeAfterInstall)
            {
                closeAfterInstall = false;
                Close();
            }
        }
    }

    private void UpdateInstallProgress(InstallProgress progress)
    {
        InstallStageText.Text = progress.Stage;
        InstallDetailText.Text = progress.Detail;
        InstallPercentText.Text = $"{progress.Percentage}%";
        InstallProgressBar.Value = progress.Percentage;
    }

    private void CancelInstallButton_Click(object sender, RoutedEventArgs e)
    {
        CancelCurrentInstall();
    }

    private void CancelCurrentInstall()
    {
        if (installCancellation is null || installCancellation.IsCancellationRequested)
        {
            return;
        }

        CancelInstallButton.IsEnabled = false;
        InstallStageText.Text = "Canceling installation";
        InstallDetailText.Text = "Removing temporary files...";
        installCancellation.Cancel();
    }

    private void PlayButton_Click(object sender, RoutedEventArgs e)
    {
        if (isGameSessionActive)
        {
            MessageBox.Show(this, "A game session is already running.", "Game running",
                MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        GameEntry? game = SelectedGame;
        if (game is null)
        {
            return;
        }

        if (!File.Exists(game.ExecutablePath))
        {
            MessageBox.Show(this, "The configured executable no longer exists. Choose a new executable in Game Settings.",
                "Executable missing", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        try
        {
            Process process = Process.Start(new ProcessStartInfo
            {
                FileName = game.ExecutablePath,
                Arguments = game.LaunchArguments,
                WorkingDirectory = Path.GetDirectoryName(game.ExecutablePath)!,
                UseShellExecute = true
            }) ?? throw new InvalidOperationException("Windows did not start the game process.");

            DateTimeOffset sessionStartedAt = DateTimeOffset.Now;
            game.LastPlayedAt = sessionStartedAt;
            game.PlayCount++;
            SaveLibrary();
            UpdatePlayStats(game);
            RefreshSelectedGame(game);
            TrackGameSession(process, game, sessionStartedAt);
            InstallDateText.Text = GetGameSubheading(game);
            StatusText.Text = $"Launched {game.Name}";
            isGameSessionActive = true;
            MinimizeToTray();
        }
        catch (Exception exception) when (exception is Win32Exception or InvalidOperationException)
        {
            MessageBox.Show(this, exception.Message, "Could not launch game", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private void TrackGameSession(Process process, GameEntry game, DateTimeOffset sessionStartedAt)
    {
        int sessionRecorded = 0;

        void CompleteSession()
        {
            if (Interlocked.Exchange(ref sessionRecorded, 1) != 0)
            {
                return;
            }

            long elapsedSeconds = Math.Max(1, (long)(DateTimeOffset.Now - sessionStartedAt).TotalSeconds);
            process.Dispose();

            if (Dispatcher.HasShutdownStarted)
            {
                return;
            }

            _ = Dispatcher.InvokeAsync(() =>
            {
                game.TotalPlayTimeSeconds = checked(game.TotalPlayTimeSeconds + elapsedSeconds);
                SaveLibrary();
                if (SelectedGame?.Id == game.Id)
                {
                    UpdatePlayStats(game);
                }
                RefreshSelectedGame(game);
                CompleteGameSession(game, TimeSpan.FromSeconds(elapsedSeconds));
            });
        }

        process.EnableRaisingEvents = true;
        process.Exited += (_, _) => CompleteSession();
        if (process.HasExited)
        {
            CompleteSession();
        }
    }

    private void MinimizeToTray()
    {
        trayIconService.Show();
        WindowState = WindowState.Minimized;
        Hide();
    }

    private void RestoreFromTray()
    {
        Show();
        WindowState = WindowState.Normal;
        Activate();
    }

    private void TrayIconService_RestoreRequested(object? sender, EventArgs e)
    {
        _ = Dispatcher.InvokeAsync(RestoreFromTray);
    }

    private void CompleteGameSession(GameEntry game, TimeSpan sessionDuration)
    {
        isGameSessionActive = false;
        trayIconService.Hide();
        RestoreFromTray();
        StatusText.Text = $"{game.Name} closed after {FormatPlayTime((long)sessionDuration.TotalSeconds)}";

        if (!appSettings.ShowPostLaunchSummaries)
        {
            return;
        }

        PostLaunchSummaryWindow summary = new(
            game.Name,
            sessionDuration,
            TimeSpan.FromSeconds(game.TotalPlayTimeSeconds),
            game.PlayCount)
        {
            Owner = this
        };
        summary.ShowDialog();

        if (!summary.ShowFutureSummaries)
        {
            appSettings.ShowPostLaunchSummaries = false;
            ShowPostLaunchSummariesMenuItem.IsChecked = false;
            settingsService.Save(appSettings);
        }
    }

    private void RevealButton_Click(object sender, RoutedEventArgs e)
    {
        GameEntry? game = SelectedGame;
        if (game is null || !Directory.Exists(game.InstallDirectory))
        {
            return;
        }

        Process.Start(new ProcessStartInfo
        {
            FileName = "explorer.exe",
            Arguments = $"/select,\"{game.ExecutablePath}\"",
            UseShellExecute = true
        });
    }

    private void ChooseExecutableButton_Click(object sender, RoutedEventArgs e)
    {
        GameEntry? game = SelectedGame;
        if (game is null)
        {
            return;
        }

        OpenFileDialog dialog = new()
        {
            Title = "Choose the game executable",
            Filter = "Windows executables (*.exe)|*.exe",
            InitialDirectory = game.InstallDirectory,
            CheckFileExists = true
        };

        if (dialog.ShowDialog(this) != true)
        {
            return;
        }

        string selectedPath = Path.GetFullPath(dialog.FileName);
        string installRoot = Path.GetFullPath(game.InstallDirectory) + Path.DirectorySeparatorChar;
        if (!selectedPath.StartsWith(installRoot, StringComparison.OrdinalIgnoreCase))
        {
            MessageBox.Show(this, "Choose an executable inside this game's install directory.",
                "Executable outside game", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        game.ExecutablePath = selectedPath;
        ApplyCompatibility(game);
        ExecutableBox.Text = selectedPath;
        SaveLibrary();
        gamesView.Refresh();
        UpdateCompatibilityDetails(game);
    }

    private void UninstallButton_Click(object sender, RoutedEventArgs e)
    {
        GameEntry? game = SelectedGame;
        if (game is null)
        {
            return;
        }

        MessageBoxResult result = MessageBox.Show(this,
            $"Uninstall {game.Name}?\n\nThis deletes its installed files but does not delete the original ZIP.",
            "Uninstall game", MessageBoxButton.YesNo, MessageBoxImage.Warning, MessageBoxResult.No);

        if (result != MessageBoxResult.Yes)
        {
            return;
        }

        try
        {
            libraryService.Uninstall(game);
            games.Remove(game);
            SaveLibrary();
            gamesView.Refresh();
            GameList.SelectedItem = gamesView.Cast<GameEntry>().FirstOrDefault();
            StatusText.Text = $"Uninstalled {game.Name}";
            UpdateLibraryState();
        }
        catch (IOException exception)
        {
            MessageBox.Show(this, exception.Message, "Could not uninstall game", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private void GameList_SelectionChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
    {
        ShowSelectedGame();
    }

    private void GameList_MouseDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (SelectedGame is not null)
        {
            PlayButton_Click(sender, e);
        }
    }

    private void ShowSelectedGame()
    {
        GameEntry? game = SelectedGame;
        EmptyState.Visibility = game is null ? Visibility.Visible : Visibility.Collapsed;
        DetailPanel.Visibility = game is null ? Visibility.Collapsed : Visibility.Visible;

        if (game is null)
        {
            return;
        }

        isUpdatingDetails = true;
        GameNameBox.Text = game.Name;
        InstallDateText.Text = GetGameSubheading(game);
        UpdatePlayStats(game);
        FavoriteCheckBox.IsChecked = game.IsFavorite;
        ArgumentsBox.Text = game.LaunchArguments;
        ExecutableBox.Text = game.ExecutablePath;
        UpdateCompatibilityDetails(game);
        isUpdatingDetails = false;
    }

    private void GameNameBox_LostFocus(object sender, RoutedEventArgs e)
    {
        SaveGameName();
    }

    private void GameNameBox_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter)
        {
            return;
        }

        SaveGameName();
        Keyboard.ClearFocus();
    }

    private void SaveGameName()
    {
        GameEntry? game = SelectedGame;
        string name = GameNameBox.Text.Trim();
        if (game is null || string.IsNullOrWhiteSpace(name) || game.Name == name)
        {
            if (game is not null)
            {
                GameNameBox.Text = game.Name;
            }
            return;
        }

        game.Name = name;
        SaveLibrary();
        gamesView.Refresh();
        GameList.SelectedItem = game;
    }

    private void ArgumentsBox_LostFocus(object sender, RoutedEventArgs e)
    {
        GameEntry? game = SelectedGame;
        if (game is null || game.LaunchArguments == ArgumentsBox.Text)
        {
            return;
        }

        game.LaunchArguments = ArgumentsBox.Text;
        SaveLibrary();
    }

    private void FavoriteCheckBox_Changed(object sender, RoutedEventArgs e)
    {
        GameEntry? game = SelectedGame;
        if (isUpdatingDetails || game is null)
        {
            return;
        }

        game.IsFavorite = FavoriteCheckBox.IsChecked == true;
        SaveLibrary();
        gamesView.Refresh();
        GameList.SelectedItem = game;
    }

    private void SearchBox_TextChanged(object sender, System.Windows.Controls.TextChangedEventArgs e)
    {
        gamesView?.Refresh();
        UpdateLibraryState();
    }

    private bool FilterGame(object item)
    {
        return item is GameEntry game
            && (string.IsNullOrWhiteSpace(SearchBox.Text)
                || game.Name.Contains(SearchBox.Text, StringComparison.OrdinalIgnoreCase));
    }

    private void Window_DragOver(object sender, DragEventArgs e)
    {
        e.Effects = GetDroppedZip(e.Data) is null ? DragDropEffects.None : DragDropEffects.Copy;
        e.Handled = true;
    }

    private void Window_Drop(object sender, DragEventArgs e)
    {
        string? archivePath = GetDroppedZip(e.Data);
        if (archivePath is not null)
        {
            _ = InstallArchiveAsync(archivePath);
        }
    }

    private static string? GetDroppedZip(IDataObject data)
    {
        if (!data.GetDataPresent(DataFormats.FileDrop) || data.GetData(DataFormats.FileDrop) is not string[] files)
        {
            return null;
        }

        return files.Length == 1 && string.Equals(Path.GetExtension(files[0]), ".zip", StringComparison.OrdinalIgnoreCase)
            ? files[0]
            : null;
    }

    private void SaveLibrary()
    {
        libraryService.Save(games);
        UpdateLibraryState();
    }

    private void UpdateLibraryState()
    {
        int visibleCount = gamesView?.Cast<object>().Count() ?? games.Count;
        LibraryCountText.Text = visibleCount == 1 ? "1 game" : $"{visibleCount} games";

        if (games.Count == 0)
        {
            EmptyState.Visibility = Visibility.Visible;
            DetailPanel.Visibility = Visibility.Collapsed;
        }
    }

    private static string GetGameSubheading(GameEntry game)
    {
        return game.LastPlayedAt is { } lastPlayed
            ? $"Last played {lastPlayed.LocalDateTime:g}"
            : $"Installed {game.InstalledAt.LocalDateTime:d}";
    }

    private void ApplyCompatibility(GameEntry game)
    {
        string executableName = Path.GetFileName(game.ExecutablePath);
        game.Compatibility = compatibilityEntries.TryGetValue(executableName, out CompatibilityInfo? compatibility)
            ? compatibility
            : null;
    }

    private void UpdateCompatibilityDetails(GameEntry game)
    {
        CompatibilityInfo? compatibility = game.Compatibility;
        const string unknown = "Unknown / Not tested";

        CompatibilityScoreText.Text = compatibility?.CompatibilityScore is int score ? $"{score}%" : unknown;
        CompatibilityRunnableText.Text = FormatCompatibilityFlag(compatibility?.Runnable, unknown);
        CompatibilityCompilableText.Text = FormatCompatibilityFlag(compatibility?.Compilable, unknown);
        CompatibilityVersionText.Text = string.IsNullOrWhiteSpace(compatibility?.Version)
            ? unknown
            : compatibility.Version;
        CompatibilityNotesText.Text = string.IsNullOrWhiteSpace(compatibility?.CompatibilityNotes)
            ? unknown
            : compatibility.CompatibilityNotes;
    }

    private static string FormatCompatibilityFlag(bool? value, string unknown)
    {
        return value switch
        {
            true => "Yes",
            false => "No",
            null => unknown
        };
    }

    private void UpdatePlayStats(GameEntry game)
    {
        PlayCountText.Text = game.PlayCount.ToString("N0");
        PlayTimeText.Text = FormatPlayTime(game.TotalPlayTimeSeconds);
    }

    private static string FormatPlayTime(long totalSeconds)
    {
        TimeSpan duration = TimeSpan.FromSeconds(Math.Max(0, totalSeconds));
        if (duration.TotalSeconds > 0 && duration.TotalMinutes < 1)
        {
            return "< 1m";
        }

        if (duration.TotalHours < 1)
        {
            return $"{(int)duration.TotalMinutes}m";
        }

        if (duration.TotalDays < 1)
        {
            return $"{(int)duration.TotalHours}h {duration.Minutes}m";
        }

        return $"{(int)duration.TotalDays}d {duration.Hours}h";
    }

    private void RefreshSelectedGame(GameEntry game)
    {
        gamesView.Refresh();
        GameList.SelectedItem = game;
    }
}