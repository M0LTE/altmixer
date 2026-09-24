using System.Drawing;
using System.Drawing.Drawing2D;
using Forms = System.Windows.Forms;

namespace AltMixer;

/// <summary>Notification-area icon: shows drift at a glance and keeps AltMixer running when the window is closed.</summary>
sealed class Tray : IDisposable
{
    readonly Forms.NotifyIcon _icon;
    readonly Icon _ok = Draw(Color.FromArgb(0x3F, 0xB9, 0x50));
    readonly Icon _warn = Draw(Color.FromArgb(0xF0, 0xB2, 0x32));
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

    /// <summary>Three mixer faders, with the status colour on the knobs.</summary>
    static Icon Draw(Color knob)
    {
        using var bmp = new Bitmap(32, 32);
        using (var g = Graphics.FromImage(bmp))
        {
            g.SmoothingMode = SmoothingMode.AntiAlias;
            using var track = new Pen(Color.FromArgb(220, 230, 230, 230), 3);
            using var fill = new SolidBrush(knob);
            int[] knobs = [20, 8, 14];
            for (var i = 0; i < 3; i++)
            {
                var x = 6 + i * 10;
                g.DrawLine(track, x, 3, x, 29);
                g.FillEllipse(fill, x - 5, knobs[i] - 5, 10, 10);
            }
        }
        return Icon.FromHandle(bmp.GetHicon());
    }
}
