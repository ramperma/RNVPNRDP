using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using WgRdp.Logging;
using WgRdp.Orchestration;
using WgRdp.Profiles;
using WgRdp.Rdp;
using WgRdp.Setup;
using WgRdp.Tray;
using WgRdp.WireGuard;

namespace WgRdp;

public partial class MainWindow : Window
{
    private ProfileStore _store = ProfileStore.Load();
    private Pipeline? _pipeline;
    private RdpSessionHost? _rdp;
    private MstscSession? _mstsc;
    private readonly AppTrayIcon _tray;
    private readonly CancellationTokenSource _lifetime = new();
    private bool _suppressProfileEvents;
    private bool _exitRequested;

    public MainWindow()
    {
        InitializeComponent();
        _tray = new AppTrayIcon();
        _tray.ShowRequested += () => Dispatcher.BeginInvoke(RestoreFromTray);
        _tray.ConnectRequested += () => Dispatcher.BeginInvoke(() => OnConnect(this, new RoutedEventArgs()));
        _tray.DisconnectRequested += () => Dispatcher.BeginInvoke(() => OnCancel(this, new RoutedEventArgs()));
        _tray.ExitRequested += () => Dispatcher.BeginInvoke(ExitFromTray);

        Log.LineWritten += entry => Dispatcher.BeginInvoke(() =>
        {
            LogList.Items.Add(entry);
            while (LogList.Items.Count > 500)
                LogList.Items.RemoveAt(0);
            LogList.ScrollIntoView(entry);
        });

        RefreshProfilePicker(selectIndex: 0);
        LoadProfileIntoUi(_store.Profiles[0]);
        Log.Info("Aplicación iniciada.");

        // El asistente no puede abrirse aquí: la ventana aún no se ha mostrado y
        // asignar Owner lanza InvalidOperationException, con lo que el proceso muere.
        Loaded += OnMainWindowLoaded;
    }

    private void OnMainWindowLoaded(object sender, RoutedEventArgs e)
    {
        Loaded -= OnMainWindowLoaded;
        if (_store.Profiles.Count > 0)
            LoadProfileIntoUi(_store.Profiles[0]);

        _ = WireGuardUi.WatchAndStopUserInterfaceAsync(_lifetime.Token);

        if (SetupState.Load().FirstRunCompleted)
            return;

        Dispatcher.BeginInvoke(OpenFirstRunWizard, DispatcherPriority.ApplicationIdle);
    }

    private void OpenFirstRunWizard()
    {
        Log.Info("Primera ejecución: abriendo asistente de configuración.");
        var wizard = new FirstRunWizard { Owner = this };
        wizard.ShowDialog();
        _store = ProfileStore.Load();
        RefreshProfilePicker(0);
        LoadProfileIntoUi(_store.Profiles[0]);
    }

    private void OnWizard(object sender, RoutedEventArgs e)
    {
        var wizard = new Setup.FirstRunWizard { Owner = this };
        if (wizard.ShowDialog() == true)
        {
            _store = ProfileStore.Load();
            RefreshProfilePicker(0);
            LoadProfileIntoUi(_store.Profiles[0]);
        }
    }

    // ---------- Perfiles ----------

    private void RefreshProfilePicker(int selectIndex)
    {
        _suppressProfileEvents = true;
        ProfilePicker.Items.Clear();
        foreach (var p in _store.Profiles)
            ProfilePicker.Items.Add(p.Name);
        ProfilePicker.SelectedIndex = Math.Min(selectIndex, _store.Profiles.Count - 1);
        _suppressProfileEvents = false;
    }

    private void OnProfileSelected(object sender, SelectionChangedEventArgs e)
    {
        if (_suppressProfileEvents) return;
        var name = ProfilePicker.SelectedItem as string;
        var profile = _store.Find(name ?? "");
        if (profile is not null)
            LoadProfileIntoUi(profile);
    }

    private void LoadProfileIntoUi(Profile p)
    {
        ProfileNameBox.Text = p.Name;
        RdpHostBox.Text = p.RdpHost;
        RdpPortBox.Text = p.RdpPort.ToString();
        UserBox.Text = p.Username;
        DomainBox.Text = p.Domain;
        TunnelNameBox.Text = p.TunnelName;
        ServerVpnIpBox.Text = p.ServerVpnIp;
        RemoveTunnelCheck.IsChecked = p.RemoveTunnelOnClose;
        RedirectPrintersCheck.IsChecked = p.RedirectPrinters;
        RedirectDrivesCheck.IsChecked = p.RedirectDrives;
        RedirectClipboardCheck.IsChecked = p.RedirectClipboard;

        var saved = SecretStore.LoadPassword(p.Name);
        PasswordBox.Password = saved ?? "";
        if (saved is not null)
            Log.Info($"Contraseña cargada del almacén para el perfil '{p.Name}'.");
    }

    private Profile ReadProfileFromUi()
    {
        var name = string.IsNullOrWhiteSpace(ProfileNameBox.Text) ? "Predeterminado" : ProfileNameBox.Text.Trim();
        var profile = _store.Find(name) ?? new Profile { Name = name };
        if (!_store.Profiles.Contains(profile))
            _store.Profiles.Add(profile);

        profile.Name = name;
        var host = RdpHostBox.Text.Trim();
        var user = UserBox.Text.Trim();
        // No pisar un perfil guardado con campos vacíos (p. ej. si la UI no pintó el texto).
        if (host.Length > 0)
            profile.RdpHost = host;
        if (user.Length > 0)
            profile.Username = user;
        profile.TunnelName = string.IsNullOrWhiteSpace(TunnelNameBox.Text) ? "wg-rdp" : TunnelNameBox.Text.Trim();
        profile.Domain = DomainBox.Text.Trim();
        profile.ServerVpnIp = ServerVpnIpBox.Text.Trim();
        profile.RemoveTunnelOnClose = RemoveTunnelCheck.IsChecked == true;
        profile.RedirectPrinters = RedirectPrintersCheck.IsChecked == true;
        profile.RedirectDrives = RedirectDrivesCheck.IsChecked == true;
        profile.RedirectClipboard = RedirectClipboardCheck.IsChecked == true;

        if (!int.TryParse(RdpPortBox.Text.Trim(), out var port) || port is < 1 or > 65535)
            port = 3389;
        profile.RdpPort = port;

        return profile;
    }

    private void OnSaveProfile(object sender, RoutedEventArgs e)
    {
        try
        {
            var profile = ReadProfileFromUi();
            _store.Save();

            if (!string.IsNullOrEmpty(PasswordBox.Password))
                SecretStore.SavePassword(profile.Name, PasswordBox.Password);

            RefreshProfilePicker(_store.Profiles.IndexOf(profile));
            SetStatus("Perfil guardado.", StatusKind.Ready);
            Log.Info($"Perfil '{profile.Name}' guardado.");
        }
        catch (Exception ex)
        {
            Log.Error("No se pudo guardar el perfil", ex);
            SetStatus("Error al guardar: " + ex.Message, StatusKind.Failed);
        }
    }

    // ---------- Conexión ----------

    private void OnConnect(object sender, RoutedEventArgs e)
    {
        RestoreFromTray();
        if (_pipeline is not null) return;

        var profile = ReadProfileFromUi();
        if (string.IsNullOrWhiteSpace(profile.RdpHost) || string.IsNullOrWhiteSpace(profile.Username))
        {
            SetStatus("Faltan el servidor o el usuario.", StatusKind.Failed);
            return;
        }

        _store.Save();

        var password = PasswordBox.Password;
        if (!string.IsNullOrEmpty(password))
            SecretStore.SavePassword(profile.Name, password);
        else
            password = SecretStore.LoadPassword(profile.Name);

        if (string.IsNullOrEmpty(password))
        {
            SetStatus("Falta la contraseña.", StatusKind.Failed);
            return;
        }

        SetUiRunning(true);
        SetStatus("Iniciando…", StatusKind.Busy);
        _pipeline = new Pipeline(profile, password);
        _pipeline.StateChanged += OnPipelineState;
        _pipeline.VpnReady += OnVpnReady;
        // Fuera del hilo de UI: evita deadlocks si algún paso bloquea o captura el SyncContext.
        var pipeline = _pipeline;
        _ = Task.Run(() => pipeline.RunAsync());
    }

    private void OnCancel(object sender, RoutedEventArgs e)
    {
        _pipeline?.Cancel();
        _rdp?.Disconnect();
        _mstsc?.Disconnect();
    }

    private void OnPipelineState(PipelineState state)
    {
        Dispatcher.BeginInvoke(() =>
        {
            var (text, kind) = state switch
            {
                PipelineState.EnsuringWireGuard => ("Comprobando WireGuard…", StatusKind.Busy),
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

    private void OnVpnReady()
    {
        // BeginInvoke (no Invoke): el pipeline no debe bloquearse esperando al hilo de UI.
        Dispatcher.BeginInvoke(() =>
        {
            var profile = ReadProfileFromUi();
            var password = SecretStore.LoadPassword(profile.Name) ?? PasswordBox.Password;

            try
            {
                StartEmbeddedRdp(profile, password);
            }
            catch (Exception ex)
            {
                Log.Warn($"Control RDP embebido no disponible ({ex.GetType().Name}: {ex.Message}). Abriendo mstsc.");
                _ = StartExternalRdpAsync(profile, password);
            }
        });
    }

    private void StartEmbeddedRdp(Profile profile, string? password)
    {
        _rdp = new RdpSessionHost();
        RdpHost.Child = _rdp.Control;
        RdpHost.Visibility = Visibility.Visible;
        PlaceholderPanel.Visibility = Visibility.Collapsed;

        _rdp.StateChanged += st =>
        {
            if (st == RdpSessionState.Disconnected)
                _pipeline?.OnRdpEnded(failed: false);
            else if (st == RdpSessionState.Failed)
                _pipeline?.OnRdpEnded(failed: true);
        };

        _rdp.Connect(profile, password);
        _pipeline?.OnRdpConnecting();
    }

    private async Task StartExternalRdpAsync(Profile profile, string? password)
    {
        try
        {
            if (string.IsNullOrEmpty(password))
                throw new InvalidOperationException("Falta la contraseña para mstsc.");

            PlaceholderText.Text = "La sesión está abierta en la ventana de Escritorio remoto.";
            PlaceholderPanel.Visibility = Visibility.Visible;
            RdpHost.Visibility = Visibility.Collapsed;

            _mstsc = new MstscSession();
            _mstsc.Ended += failed => Dispatcher.BeginInvoke(() => _pipeline?.OnRdpEnded(failed));
            await _mstsc.StartAsync(profile, password);
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
        _tray.SetSessionRunning(running);
        if (!running)
        {
            _pipeline = null;
            if (_rdp is not null)
            {
                _rdp.Dispose();
                _rdp = null;
                RdpHost.Child = null;
                RdpHost.Visibility = Visibility.Collapsed;
                PlaceholderText.Text = "Pulse Conectar para iniciar la sesión.";
                PlaceholderPanel.Visibility = Visibility.Visible;
            }
            if (_mstsc is not null)
            {
                _mstsc.Dispose();
                _mstsc = null;
            }
        }
    }

    protected override void OnClosing(System.ComponentModel.CancelEventArgs e)
    {
        if (!_exitRequested)
        {
            e.Cancel = true;
            Hide();
            _tray.NotifyHiddenOnce();
            return;
        }

        _lifetime.Cancel();
        _pipeline?.Cancel();
        _rdp?.Dispose();
        _mstsc?.Dispose();
        _tray.Dispose();
        _lifetime.Dispose();
        base.OnClosing(e);
    }

    protected override void OnClosed(EventArgs e)
    {
        base.OnClosed(e);
        Application.Current.Shutdown();
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
        _mstsc?.Disconnect();
        Close();
    }

    private void OnClearLog(object sender, RoutedEventArgs e) => LogList.Items.Clear();

    private void OnCopyLog(object sender, RoutedEventArgs e)
    {
        var sb = new StringBuilder();
        foreach (var item in LogList.Items)
        {
            if (item is LogEntry entry)
                sb.AppendLine($"{entry.Time:HH:mm:ss} [{entry.Level}] {entry.Message}");
        }

        if (sb.Length == 0)
            return;
        Clipboard.SetText(sb.ToString());
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
        StatusDot.Fill = (Brush)new BrushConverter().ConvertFromString(dot)!;
        StatusBadge.Background = (Brush)new BrushConverter().ConvertFromString(badge)!;
        _tray.SetStatus(text, kind switch
        {
            StatusKind.Busy => TrayStatusKind.Busy,
            StatusKind.Active => TrayStatusKind.Active,
            StatusKind.Failed => TrayStatusKind.Failed,
            _ => TrayStatusKind.Ready,
        });
    }
}
