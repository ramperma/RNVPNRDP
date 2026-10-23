using System.Windows.Forms;

namespace WgRdp.Tray;

public enum TrayStatusKind
{
    Ready,
    Busy,
    Active,
    Failed,
}

/// <summary>Icono de bandeja de WG-RDP con el estado de VPN/RDP.</summary>
public sealed class AppTrayIcon : IDisposable
{
    private readonly NotifyIcon _notify;
    private readonly ToolStripMenuItem _showItem;
    private readonly ToolStripMenuItem _connectItem;
    private readonly ToolStripMenuItem _disconnectItem;
    private readonly ToolStripMenuItem _exitItem;
    private bool _hiddenBalloonShown;
    private bool _disposed;

    public event Action? ShowRequested;
    public event Action? ConnectRequested;
    public event Action? DisconnectRequested;
    public event Action? ExitRequested;

    public AppTrayIcon()
    {
        _showItem = new ToolStripMenuItem("Mostrar WG-RDP");
        _connectItem = new ToolStripMenuItem("Conectar y abrir RDP");
        _disconnectItem = new ToolStripMenuItem("Cancelar / desconectar") { Enabled = false };
        _exitItem = new ToolStripMenuItem("Salir");

        _showItem.Click += (_, _) => ShowRequested?.Invoke();
        _connectItem.Click += (_, _) => ConnectRequested?.Invoke();
        _disconnectItem.Click += (_, _) => DisconnectRequested?.Invoke();
        _exitItem.Click += (_, _) => ExitRequested?.Invoke();

        var menu = new ContextMenuStrip();
        menu.Items.Add(_showItem);
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add(_connectItem);
        menu.Items.Add(_disconnectItem);
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add(_exitItem);

        _notify = new NotifyIcon
        {
            Icon = TrayIcons.Idle,
            Text = Truncate("WG-RDP — Listo"),
            Visible = true,
            ContextMenuStrip = menu,
        };
        _notify.DoubleClick += (_, _) => ShowRequested?.Invoke();
    }

    public void SetStatus(string text, TrayStatusKind kind)
    {
        if (_disposed) return;
        _notify.Icon = kind switch
        {
            TrayStatusKind.Busy => TrayIcons.Busy,
            TrayStatusKind.Active => TrayIcons.Active,
            TrayStatusKind.Failed => TrayIcons.Failed,
            _ => TrayIcons.Idle,
        };
        _notify.Text = Truncate("WG-RDP — " + text);
    }

    public void SetSessionRunning(bool running)
    {
        if (_disposed) return;
        _connectItem.Enabled = !running;
        _disconnectItem.Enabled = running;
    }

    public void NotifyHiddenOnce()
    {
        if (_disposed || _hiddenBalloonShown)
            return;
        _hiddenBalloonShown = true;
        _notify.ShowBalloonTip(
            4000,
            "WG-RDP",
            "Sigue en ejecución en la bandeja. El estado de la conexión se muestra aquí. Use Salir para cerrar.",
            ToolTipIcon.Info);
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _notify.Visible = false;
        _notify.Dispose();
    }

    private static string Truncate(string text) =>
        text.Length <= 63 ? text : text[..60] + "…";
}
