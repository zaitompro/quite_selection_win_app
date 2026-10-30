using Forms = System.Windows.Forms;

namespace QuietSelection.App.Views;

/// <summary>
/// Notification area icon. Left click opens settings; the menu stops reading or exits.
/// Reading commands are not offered here: opening the menu moves focus away from the selection.
/// </summary>
internal sealed class TrayIcon : IDisposable
{
    private readonly Forms.NotifyIcon icon;
    private readonly Forms.ToolStripMenuItem stop;
    private readonly System.Drawing.Icon idleImage, activeImage;
    private bool busy;

    internal TrayIcon(Action openSettings, Action stopReading, Action exit)
    {
        int size = Forms.SystemInformation.SmallIconSize.Width;
        idleImage = AppIcon.Tray(size, active: false);
        activeImage = AppIcon.Tray(size, active: true);
        var menu = new Forms.ContextMenuStrip();
        menu.Items.Add(new Forms.ToolStripMenuItem("QuietSelection " + Platform.AppInfo.Version) { Enabled = false });
        menu.Items.Add(new Forms.ToolStripSeparator());
        stop = new Forms.ToolStripMenuItem("Остановить чтение", null, (_, _) => stopReading()) { Enabled = false };
        menu.Items.Add(stop);
        menu.Items.Add(new Forms.ToolStripMenuItem("Настройки…", null, (_, _) => openSettings()) { Font = new(menu.Font, System.Drawing.FontStyle.Bold) });
        menu.Items.Add(new Forms.ToolStripSeparator());
        menu.Items.Add(new Forms.ToolStripMenuItem("Завершить", null, (_, _) => exit()));
        icon = new() { Text = "QuietSelection", Icon = idleImage, ContextMenuStrip = menu, Visible = true };
        icon.MouseClick += (_, e) => { if (e.Button == Forms.MouseButtons.Left) openSettings(); };
    }

    internal void SetState(bool reading, string status)
    {
        if (reading != busy) { busy = reading; icon.Icon = reading ? activeImage : idleImage; stop.Enabled = reading; }
        // The tooltip is limited to 127 characters.
        string text = "QuietSelection — " + status;
        icon.Text = text.Length > 127 ? text[..126] + "…" : text;
    }

    internal void Notify(string message) => icon.ShowBalloonTip(3000, "QuietSelection", message, Forms.ToolTipIcon.Info);

    public void Dispose() { icon.Visible = false; icon.Dispose(); idleImage.Dispose(); activeImage.Dispose(); }
}
