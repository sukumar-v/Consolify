using System.Windows.Forms;
using Loungepad.Services;

namespace Loungepad;

/// <summary>
/// The notification-area icon: a way back to the launcher, the update, and a way out.
///
/// The window has no taskbar button (ShowInTaskbar=false, so a game never has one beside it), and
/// while a game is running the launcher is hidden rather than minimized. Before this there was no
/// sign on the desktop that it was running at all, and Task Manager was the only way to quit it.
///
/// WinForms' NotifyIcon, because WPF has none of its own and the menu it shows closes when you
/// click elsewhere, which a WPF ContextMenu on a tray icon has to be coaxed into. It runs happily
/// on the WPF dispatcher, which pumps the messages its hidden window needs.
/// </summary>
public sealed class TrayIcon : IDisposable
{
    private readonly NotifyIcon _icon;
    private readonly ContextMenuStrip _menu;
    private readonly ToolStripMenuItem _update;

    public TrayIcon(Action show, Action update, Action quit)
    {
        _menu = new ContextMenuStrip();
        var showItem = new ToolStripMenuItem("Show Loungepad", null, (_, _) => show());
        showItem.Font = new System.Drawing.Font(showItem.Font, System.Drawing.FontStyle.Bold);
        _update = new ToolStripMenuItem("Check for updates", null, (_, _) => update());
        _menu.Items.AddRange(new ToolStripItem[]
        {
            showItem,
            new ToolStripSeparator(),
            _update,
            new ToolStripSeparator(),
            new ToolStripMenuItem("Quit Loungepad", null, (_, _) => quit()),
        });

        _icon = new NotifyIcon
        {
            Text = $"Loungepad {UpdateService.Format(UpdateService.Current)}",
            Icon = LoadIcon(),
            ContextMenuStrip = _menu,
            Visible = true,
        };
        // A left click is "take me back", which is what most people click a tray icon for; the
        // menu is on the right button.
        _icon.MouseClick += (_, e) => { if (e.Button == MouseButtons.Left) show(); };
    }

    /// <summary>The update item says what pressing it will do.</summary>
    public void SetUpdate(UpdateStatus s)
    {
        (_update.Text, _update.Enabled) = s.State switch
        {
            "checking" => ("Checking for updates…", false),
            "upToDate" => ("Up to date — check again", true),
            "available" => ($"Update to {s.Latest}", true),
            "downloading" => ($"Downloading {s.Latest}… {s.Progress}%", false),
            "ready" => ($"Restart to update to {s.Latest}", true),
            "failed" => ("Update failed — try again", true),
            "unsupported" => ("Updates unavailable for this copy", false),
            _ => ("Check for updates", true),
        };
    }

    private static System.Drawing.Icon LoadIcon()
    {
        try
        {
            var res = System.Windows.Application.GetResourceStream(new Uri("pack://application:,,,/Loungepad.ico"));
            if (res is not null)
                using (res.Stream)
                    return new System.Drawing.Icon(res.Stream, SystemInformation.SmallIconSize);
        }
        catch { /* fall back to the exe's own */ }
        return System.Drawing.Icon.ExtractAssociatedIcon(Environment.ProcessPath!) ?? System.Drawing.SystemIcons.Application;
    }

    public void Dispose()
    {
        // Hidden first: a disposed icon that is still Visible stays in the tray as a ghost until
        // the pointer passes over it.
        _icon.Visible = false;
        _icon.Dispose();
        _menu.Dispose();
    }
}
