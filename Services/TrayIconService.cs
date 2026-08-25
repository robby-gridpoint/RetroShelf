namespace RetroShelf.Services;

public sealed class TrayIconService : IDisposable
{
    private readonly System.Drawing.Icon? applicationIcon;
    private readonly System.Windows.Forms.NotifyIcon notifyIcon;

    public TrayIconService()
    {
        applicationIcon = Environment.ProcessPath is { } processPath
            ? System.Drawing.Icon.ExtractAssociatedIcon(processPath)
            : null;

        notifyIcon = new System.Windows.Forms.NotifyIcon
        {
            Icon = applicationIcon ?? System.Drawing.SystemIcons.Application,
            Text = "RetroShelf - game running"
        };
        notifyIcon.DoubleClick += NotifyIcon_DoubleClick;
    }

    public event EventHandler? RestoreRequested;

    public void Show()
    {
        notifyIcon.Visible = true;
    }

    public void Hide()
    {
        notifyIcon.Visible = false;
    }

    public void Dispose()
    {
        notifyIcon.Visible = false;
        notifyIcon.DoubleClick -= NotifyIcon_DoubleClick;
        notifyIcon.Dispose();
        applicationIcon?.Dispose();
    }

    private void NotifyIcon_DoubleClick(object? sender, EventArgs e)
    {
        RestoreRequested?.Invoke(this, EventArgs.Empty);
    }
}
