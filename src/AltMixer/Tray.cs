using System.Drawing;
using System.Drawing.Drawing2D;
using Forms = System.Windows.Forms;

namespace AltMixer;

/// <summary>Notification-area icon: shows drift at a glance and keeps AltMixer running when the window is closed.</summary>
sealed class Tray : IDisposable
{
    readonly Forms.NotifyIcon _icon;
    readonly Icon _ok = AppIcon();
    readonly Icon _warn = WithBadge(AppIcon(), Color.FromArgb(0xF0, 0xB2, 0x32));
    bool? _drifted;

    public Tray(Action show, Action restoreAll, Action exit)
    {
        var menu = new Forms.ContextMenuStrip();
        menu.Items.Add("Open AltMixer", null, (_, _) => show());
        menu.Items.Add("Restore all", null, (_, _) => restoreAll());
        menu.Items.Add(new Forms.ToolStripSeparator());
        menu.Items.Add("Exit", null, (_, _) => exit());
        _icon = new Forms.NotifyIcon { ContextMenuStrip = menu, Visible = true };
        _icon.MouseClick += (_, e) => { if (e.Button == Forms.MouseButtons.Left) show(); };
        _icon.BalloonTipClicked += (_, _) => show();
        Update(0);
    }

    public void Update(int driftCount)
    {
        var drifted = driftCount > 0;
        _icon.Text = drifted ? $"AltMixer: {driftCount} setting{(driftCount == 1 ? "" : "s")} changed" : "AltMixer: everything is as you set it";
        if (_drifted == drifted) return;
        _drifted = drifted;
        _icon.Icon = drifted ? _warn : _ok;
    }

    public void Notify(string text) => _icon.ShowBalloonTip(4000, "AltMixer", text, Forms.ToolTipIcon.Warning);

    public void Dispose()
    {
        _icon.Visible = false;
        _icon.Dispose();
    }

    /// <summary>The app icon at the notification area's size.</summary>
    static Icon AppIcon()
    {
        using var stream = System.Windows.Application.GetResourceStream(new Uri("pack://application:,,,/AltMixer.ico")).Stream;
        return new Icon(stream, Forms.SystemInformation.SmallIconSize);
    }

    /// <summary>The icon with a status dot in the bottom-right corner.</summary>
    static Icon WithBadge(Icon icon, Color colour)
    {
        using var bmp = icon.ToBitmap();
        using (var g = Graphics.FromImage(bmp))
        {
            g.SmoothingMode = SmoothingMode.AntiAlias;
            var d = bmp.Width * 0.55f;
            var x = bmp.Width - d;
            using var outline = new SolidBrush(Color.FromArgb(0x20, 0x20, 0x20));
            using var fill = new SolidBrush(colour);
            g.FillEllipse(outline, x - 1, x - 1, d + 1, d + 1);
            g.FillEllipse(fill, x, x, d - 1, d - 1);
        }
        return Icon.FromHandle(bmp.GetHicon());
    }
}
