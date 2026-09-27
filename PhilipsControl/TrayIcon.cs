using System.Drawing;
using Forms = System.Windows.Forms;

namespace PhilipsControl;

/// <summary>Notification-area icon with quick TV controls, so the remote stays one click away while the window is hidden.</summary>
internal sealed class TrayIcon : IDisposable
{
    private readonly Forms.NotifyIcon _icon;
    private readonly Forms.ToolStripMenuItem _header;
    private readonly Forms.ToolStripMenuItem _power;

    public TrayIcon(Action show, Action<string> key, Action power, Action<int> sleep, Action exit)
    {
        var menu = new Forms.ContextMenuStrip { Renderer = new DarkRenderer(), ShowImageMargin = false, Font = new Font("Consolas", 9.5f) };
        _header = new Forms.ToolStripMenuItem("No TV connected") { Enabled = false };
        _power = new Forms.ToolStripMenuItem("Power", null, (_, _) => power());
        var sleepMenu = new Forms.ToolStripMenuItem("Sleep timer");
        foreach (var minutes in new[] { 0, 15, 30, 60, 90, 120 })
            sleepMenu.DropDownItems.Add(new Forms.ToolStripMenuItem(minutes == 0 ? "Off" : $"{minutes} min", null, (_, _) => sleep(minutes)));
        ((Forms.ToolStripDropDownMenu)sleepMenu.DropDown).ShowImageMargin = false;
        sleepMenu.DropDown.Renderer = menu.Renderer;

        menu.Items.AddRange(
        [
            _header,
            new Forms.ToolStripSeparator(),
            new Forms.ToolStripMenuItem("Open Philips Control", null, (_, _) => show()) { Font = new Font("Consolas", 9.5f, System.Drawing.FontStyle.Bold) },
            new Forms.ToolStripSeparator(),
            _power,
            new Forms.ToolStripMenuItem("Volume +", null, (_, _) => key("VolumeUp")),
            new Forms.ToolStripMenuItem("Volume -", null, (_, _) => key("VolumeDown")),
            new Forms.ToolStripMenuItem("Mute", null, (_, _) => key("Mute")),
            new Forms.ToolStripMenuItem("Play / Pause", null, (_, _) => key("PlayPause")),
            new Forms.ToolStripMenuItem("Ambilight on/off", null, (_, _) => key("AmbilightOnOff")),
            sleepMenu,
            new Forms.ToolStripSeparator(),
            new Forms.ToolStripMenuItem("Exit", null, (_, _) => exit())
        ]);

        using var stream = System.Windows.Application.GetResourceStream(new Uri("pack://application:,,,/Assets/app.ico"))?.Stream;
        _icon = new Forms.NotifyIcon
        {
            Icon = stream is null ? SystemIcons.Application : new Icon(stream, 16, 16),
            Text = "Philips Control",
            ContextMenuStrip = menu,
            Visible = true
        };
        _icon.MouseClick += (_, e) => { if (e.Button == Forms.MouseButtons.Left) show(); };
    }

    public void Update(string tvName, string state, bool poweredOn)
    {
        _header.Text = $"{tvName} · {state}";
        _power.Text = poweredOn ? "Power off (standby)" : "Power on";
        var tip = $"Philips Control\n{tvName} · {state}";
        _icon.Text = tip.Length > 63 ? tip[..63] : tip;
    }

    public void Balloon(string title, string text)
    {
        _icon.BalloonTipTitle = title;
        _icon.BalloonTipText = text;
        _icon.ShowBalloonTip(3000);
    }

    public void Dispose()
    {
        _icon.Visible = false;
        _icon.ContextMenuStrip?.Dispose();
        _icon.Dispose();
    }

    private sealed class DarkRenderer : Forms.ToolStripProfessionalRenderer
    {
        private static readonly Color Back = Color.FromArgb(12, 16, 12);
        private static readonly Color Hover = Color.FromArgb(25, 49, 29);
        private static readonly Color Border = Color.FromArgb(38, 51, 41);
        private static readonly Color Text = Color.FromArgb(212, 222, 211);
        private static readonly Color Dim = Color.FromArgb(101, 227, 148);

        public DarkRenderer() : base(new Colors()) { RoundedEdges = false; }

        protected override void OnRenderItemText(Forms.ToolStripItemTextRenderEventArgs e)
        {
            e.TextColor = e.Item.Enabled ? Text : Dim;
            base.OnRenderItemText(e);
        }

        protected override void OnRenderArrow(Forms.ToolStripArrowRenderEventArgs e)
        {
            e.ArrowColor = Text;
            base.OnRenderArrow(e);
        }

        private sealed class Colors : Forms.ProfessionalColorTable
        {
            public override Color ToolStripDropDownBackground => Back;
            public override Color MenuBorder => Border;
            public override Color MenuItemBorder => Hover;
            public override Color MenuItemSelected => Hover;
            public override Color SeparatorDark => Border;
            public override Color SeparatorLight => Back;
            public override Color ImageMarginGradientBegin => Back;
            public override Color ImageMarginGradientMiddle => Back;
            public override Color ImageMarginGradientEnd => Back;
        }
    }
}
