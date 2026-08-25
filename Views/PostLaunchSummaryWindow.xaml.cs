using System.Windows;

namespace RetroShelf.Views;

public partial class PostLaunchSummaryWindow : Window
{
    public PostLaunchSummaryWindow(string gameName, TimeSpan sessionTime, TimeSpan totalPlayTime, int launchCount)
    {
        InitializeComponent();
        GameNameText.Text = gameName;
        SessionTimeText.Text = FormatDuration(sessionTime);
        TotalPlayTimeText.Text = FormatDuration(totalPlayTime);
        LaunchCountText.Text = launchCount.ToString("N0");
    }

    public bool ShowFutureSummaries => ShowFutureSummariesCheckBox.IsChecked == true;

    private static string FormatDuration(TimeSpan duration)
    {
        if (duration.TotalMinutes < 1)
        {
            return $"{Math.Max(1, (int)duration.TotalSeconds)} sec";
        }

        if (duration.TotalHours < 1)
        {
            return $"{(int)duration.TotalMinutes} min";
        }

        return $"{(int)duration.TotalHours}h {duration.Minutes}m";
    }

    private void OkButton_Click(object sender, RoutedEventArgs e)
    {
        Close();
    }
}
