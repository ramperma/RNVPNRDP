using System.Collections.ObjectModel;
using System.Text;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Platform;
using Avalonia.Threading;
using WgRdp.Logging;
using WgRdp.Orchestration;
using WgRdp.Profiles;
using WgRdp.Rdp;
using WgRdp.Setup;
using WgRdp.Views;

namespace WgRdp;

public partial class MainWindow : Window
{
    private ProfileStore _store = ProfileStore.Load();
    private Pipeline? _pipeline;
    private FreeRdpSession? _rdp;
    private readonly ObservableCollection<LogEntry> _logs = [];
    private readonly CancellationTokenSource _lifetime = new();
    private TrayIcon? _tray;
    private NativeMenuItem? _connectItem;
    private NativeMenuItem? _disconnectItem;
    private bool _suppressProfileEvents;
    private bool _exitRequested;
    private bool _trayAvailable;
    private bool _opened;

    public MainWindow()
    {
        InitializeComponent();
        LogList.ItemsSource = _logs;
        TryCreateTray();
        Log.LineWritten += entry => Dispatcher.UIThread.Post(() =>
        {
            _logs.Add(entry);
            while (_logs.Count > 500)
                _logs.RemoveAt(0);
            LogList.ScrollIntoView(entry);
        });

        RefreshProfilePicker(0);
        LoadProfileIntoUi(_store.Profiles[0]);
        Log.Info("Aplicación iniciada (Linux).");
        Opened += OnMainWindowOpened;
    }

    private void TryCreateTray()
    {
        try
        {
            using var raw = AssetLoader.Open(new Uri("avares://wgrdp/Assets/tray.png"));
            var copy = new MemoryStream();
            raw.CopyTo(copy);
            copy.Position = 0;
            var icon = new WindowIcon(copy);
            Icon = icon;

            copy.Position = 0;
            var show = new NativeMenuItem("Mostrar WG-RDP");
            _connectItem = new NativeMenuItem("Conectar y abrir RDP");
            _disconnectItem = new NativeMenuItem("Cancelar / desconectar") { IsEnabled = false };
            var exit = new NativeMenuItem("Salir");
            show.Click += (_, _) => Dispatcher.UIThread.Post(RestoreFromTray);
            _connectItem.Click += (_, _) => Dispatcher.UIThread.Post(() => OnConnect(this, new RoutedEventArgs()));
            _disconnectItem.Click += (_, _) => Dispatcher.UIThread.Post(() => OnCancel(this, new RoutedEventArgs()));
            exit.Click += (_, _) => Dispatcher.UIThread.Post(ExitFromTray);

            var menu = new NativeMenu();
            menu.Items.Add(show);
            menu.Items.Add(_connectItem);
            menu.Items.Add(_disconnectItem);
            menu.Items.Add(new NativeMenuItemSeparator());
            menu.Items.Add(exit);

            _tray = new TrayIcon
            {
                Icon = new WindowIcon(copy),
                ToolTipText = "WG-RDP — Listo",
                IsVisible = true,
                Menu = menu,
            };
            _tray.Clicked += (_, _) => Dispatcher.UIThread.Post(RestoreFromTray);
            TrayIcon.SetIcons(Application.Current!, new TrayIcons { _tray });
            _trayAvailable = true;
        }
        catch (Exception ex)
        {
            Log.Warn("No se pudo crear el icono de bandeja: " + ex.Message);
            _trayAvailable = false;
        }
    }

    private async void OnMainWindowOpened(object? sender, EventArgs e)
    {
        if (_opened)
            return;
        _opened = true;
        if (_store.Profiles.Count > 0)
            LoadProfileIntoUi(_store.Profiles[0]);
        if (!SetupState.Load().FirstRunCompleted)
            await OpenWizardAsync();
    }

    private async Task OpenWizardAsync()
    {
        Log.Info("Abriendo asistente de configuración.");
        var wizard = new FirstRunWizard();
        var completed = await wizard.ShowDialog<bool>(this);
        if (!completed)
            return;
        _store = ProfileStore.Load();
        RefreshProfilePicker(0);
        LoadProfileIntoUi(_store.Profiles[0]);
    }

    private async void OnWizard(object? sender, RoutedEventArgs e) => await OpenWizardAsync();

    private void RefreshProfilePicker(int selectIndex)
    {
        _suppressProfileEvents = true;
        ProfilePicker.Items.Clear();
        foreach (var profile in _store.Profiles)
            ProfilePicker.Items.Add(profile.Name);
        ProfilePicker.SelectedIndex = Math.Min(selectIndex, _store.Profiles.Count - 1);
        _suppressProfileEvents = false;
    }

    private void OnProfileSelected(object? sender, SelectionChangedEventArgs e)
    {
        if (_suppressProfileEvents)
            return;
        var name = ProfilePicker.SelectedItem as string;
        var profile = _store.Find(name ?? "");
        if (profile is not null)
            LoadProfileIntoUi(profile);
    }

    private void LoadProfileIntoUi(Profile profile)
    {
        ProfileNameBox.Text = profile.Name;
        RdpHostBox.Text = profile.RdpHost;
        RdpPortBox.Text = profile.RdpPort.ToString();
        UserBox.Text = profile.Username;
        DomainBox.Text = profile.Domain;
        TunnelNameBox.Text = profile.TunnelName;
        ServerVpnIpBox.Text = profile.ServerVpnIp;
        RemoveTunnelCheck.IsChecked = profile.RemoveTunnelOnClose;
        RedirectPrintersCheck.IsChecked = profile.RedirectPrinters;
        RedirectDrivesCheck.IsChecked = profile.RedirectDrives;
        RedirectClipboardCheck.IsChecked = profile.RedirectClipboard;

        var saved = SecretStore.LoadPassword(profile.Name);
        PasswordBox.Text = saved ?? "";
        if (saved is not null)
            Log.Info($"Contraseña cargada del almacén para el perfil '{profile.Name}'.");
    }

    private Profile ReadProfileFromUi()
    {
        var name = string.IsNullOrWhiteSpace(ProfileNameBox.Text) ? "Predeterminado" : ProfileNameBox.Text.Trim();
        var profile = _store.Find(name) ?? new Profile { Name = name };
        if (!_store.Profiles.Contains(profile))
            _store.Profiles.Add(profile);

        profile.Name = name;
        var host = RdpHostBox.Text?.Trim() ?? "";
        var user = UserBox.Text?.Trim() ?? "";
        if (host.Length > 0)
            profile.RdpHost = host;
        if (user.Length > 0)
            profile.Username = user;
        profile.TunnelName = string.IsNullOrWhiteSpace(TunnelNameBox.Text) ? "wg-rdp" : TunnelNameBox.Text.Trim();
        profile.Domain = DomainBox.Text?.Trim() ?? "";
        profile.ServerVpnIp = ServerVpnIpBox.Text?.Trim() ?? "";
        profile.RemoveTunnelOnClose = RemoveTunnelCheck.IsChecked == true;
        profile.RedirectPrinters = RedirectPrintersCheck.IsChecked == true;
        profile.RedirectDrives = RedirectDrivesCheck.IsChecked == true;
        profile.RedirectClipboard = RedirectClipboardCheck.IsChecked == true;
        if (!int.TryParse(RdpPortBox.Text?.Trim(), out var port) || port is < 1 or > 65535)
            port = 3389;
        profile.RdpPort = port;
        return profile;
    }

    private async void OnSaveProfile(object? sender, RoutedEventArgs e)
    {
        try
        {
            var profile = ReadProfileFromUi();
            _store.Save();
            if (!string.IsNullOrEmpty(PasswordBox.Text))
                SecretStore.SavePassword(profile.Name, PasswordBox.Text);
            RefreshProfilePicker(_store.Profiles.IndexOf(profile));
            SetStatus("Perfil guardado.", StatusKind.Ready);
            Log.Info($"Perfil '{profile.Name}' guardado.");
        }
        catch (Exception ex)
        {
            Log.Error("No se pudo guardar el perfil", ex);
            SetStatus("Error al guardar: " + ex.Message, StatusKind.Failed);
            await Dialogs.ShowMessageAsync(this, "No se pudo guardar", ex.Message);
        }
    }

    private void OnConnect(object? sender, RoutedEventArgs e)
    {
        RestoreFromTray();
        if (_pipeline is not null)
            return;

        var profile = ReadProfileFromUi();
        if (string.IsNullOrWhiteSpace(profile.RdpHost) || string.IsNullOrWhiteSpace(profile.Username))
        {
            SetStatus("Faltan el servidor o el usuario.", StatusKind.Failed);
            return;
        }

        _store.Save();
        var password = PasswordBox.Text ?? "";
        if (password.Length > 0)
            SecretStore.SavePassword(profile.Name, password);
        else
            password = SecretStore.LoadPassword(profile.Name) ?? "";

        if (password.Length == 0)
        {
            SetStatus("Falta la contraseña.", StatusKind.Failed);
            return;
        }

        SetUiRunning(true);
        SetStatus("Iniciando…", StatusKind.Busy);
        _pipeline = new Pipeline(profile, password);
        _pipeline.StateChanged += OnPipelineState;
        _pipeline.VpnReady += () => OnVpnReady(password);
        var pipeline = _pipeline;
        _ = Task.Run(() => pipeline.RunAsync());
    }

    private void OnCancel(object? sender, RoutedEventArgs e)
    {
        _pipeline?.Cancel();
        _rdp?.Disconnect();
    }

    private void OnPipelineState(PipelineState state)
    {
        Dispatcher.UIThread.Post(() =>
        {
            var (text, kind) = state switch
            {
                PipelineState.EnsuringWireGuard => ("Comprobando WireGuard y FreeRDP…", StatusKind.Busy),
                PipelineState.ImportingTunnel => ("Importando túnel…", StatusKind.Busy),
                PipelineState.ConnectingVpn => ("Conectando VPN…", StatusKind.Busy),
                PipelineState.WaitingHandshake => ("Esperando enlace VPN…", StatusKind.Busy),
                PipelineState.ConnectingRdp => ("Abriendo escritorio remoto…", StatusKind.Busy),
                PipelineState.SessionActive => ("Sesión activa", StatusKind.Active),
                PipelineState.TearingDown => ("Cerrando túnel…", StatusKind.Busy),
                PipelineState.Finished => ("Listo", StatusKind.Ready),
                PipelineState.Failed => ("Error: " + (_pipeline?.LastError ?? "desconocido"), StatusKind.Failed),
                _ => ("Listo", StatusKind.Ready),
            };
            SetStatus(text, kind);
            if (state is PipelineState.Finished or PipelineState.Failed)
                SetUiRunning(false);
        });
    }

    private void OnVpnReady(string password)
    {
        Dispatcher.UIThread.Post(() =>
        {
            var profile = ReadProfileFromUi();
            var secret = SecretStore.LoadPassword(profile.Name);
            if (string.IsNullOrEmpty(secret))
                secret = password;
            _ = StartFreeRdpAsync(profile, secret);
        });
    }

    private async Task StartFreeRdpAsync(Profile profile, string? password)
    {
        try
        {
            if (string.IsNullOrEmpty(password))
                throw new InvalidOperationException("Falta la contraseña para FreeRDP.");

            PlaceholderText.Text = "La sesión está abierta en la ventana de Remmina.";
            _rdp = new FreeRdpSession();
            _rdp.Ended += failed => Dispatcher.UIThread.Post(() => _pipeline?.OnRdpEnded(failed));
            await _rdp.StartAsync(profile, password);
            _pipeline?.OnRdpConnecting();
        }
        catch (Exception ex)
        {
            Log.Error("No se pudo iniciar la sesión RDP", ex);
            _pipeline?.OnRdpEnded(failed: true);
        }
    }

    private void SetUiRunning(bool running)
    {
        ConnectButton.IsEnabled = !running;
        CancelButton.IsEnabled = running;
        SaveButton.IsEnabled = !running;
        if (_connectItem is not null)
            _connectItem.IsEnabled = !running;
        if (_disconnectItem is not null)
            _disconnectItem.IsEnabled = running;
        if (!running)
        {
            _pipeline = null;
            _rdp?.Dispose();
            _rdp = null;
            PlaceholderText.Text = "Pulse Conectar para iniciar la sesión. El escritorio se abre en una ventana de Remmina.";
        }
    }

    protected override void OnClosing(WindowClosingEventArgs e)
    {
        if (!_exitRequested && _trayAvailable)
        {
            e.Cancel = true;
            Hide();
            Log.Info("La ventana sigue en la bandeja. Use Salir para cerrar del todo.");
            return;
        }

        _lifetime.Cancel();
        _pipeline?.Cancel();
        _rdp?.Dispose();
        if (_tray is not null)
        {
            _tray.IsVisible = false;
            _tray.Dispose();
            _tray = null;
        }
        _lifetime.Dispose();
        base.OnClosing(e);
    }

    private void RestoreFromTray()
    {
        Show();
        if (WindowState == WindowState.Minimized)
            WindowState = WindowState.Normal;
        Activate();
    }

    private void ExitFromTray()
    {
        _exitRequested = true;
        _pipeline?.Cancel();
        _rdp?.Disconnect();
        Close();
    }

    private void OnClearLog(object? sender, RoutedEventArgs e) => _logs.Clear();

    private async void OnCopyLog(object? sender, RoutedEventArgs e)
    {
        var sb = new StringBuilder();
        foreach (var entry in _logs)
            sb.AppendLine($"{entry.Time:HH:mm:ss} [{entry.Level}] {entry.Message}");
        if (sb.Length == 0)
            return;

        var clipboard = (this as TopLevel)?.Clipboard;
        if (clipboard is null)
            return;
        await clipboard.SetTextAsync(sb.ToString());
        SetStatus("Registro copiado.", StatusKind.Ready);
    }

    private enum StatusKind { Ready, Busy, Active, Failed }

    private void SetStatus(string text, StatusKind kind)
    {
        StatusText.Text = text;
        var (dot, badge) = kind switch
        {
            StatusKind.Busy => ("#3D9CF0", "#1A2A3C"),
            StatusKind.Active => ("#3DDC97", "#16342C"),
            StatusKind.Failed => ("#E06C75", "#3A2226"),
            _ => ("#8B9BB0", "#222C38"),
        };
        StatusDot.Background = Brush.Parse(dot);
        StatusBadge.Background = Brush.Parse(badge);
        if (_tray is not null)
            _tray.ToolTipText = Truncate("WG-RDP — " + text);
    }

    private static string Truncate(string text) => text.Length <= 120 ? text : text[..117] + "...";
}
