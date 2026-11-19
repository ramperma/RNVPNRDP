using System.Collections.ObjectModel;
using System.Text;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Platform;
using Avalonia.Threading;
using WgRdp.Linux;
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
    private string _selectedProfileId = "";
    private Profile? _activeProfile;
    private Pipeline? _pipeline;
    private FreeRdpSession? _rdp;
    private readonly ObservableCollection<LogEntry> _logs = [];
    private readonly CancellationTokenSource _lifetime = new();
    private TrayIcon? _tray;
    private NativeMenuItem? _connectItem;
    private NativeMenuItem? _disconnectItem;
    private bool _suppressProfileEvents;
    private bool _switching;
    private bool _exitRequested;
    private bool _exiting;
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

        var initial = _store.Profiles[0];
        RefreshProfilePicker(initial.Id);
        LoadProfileIntoUi(initial);
        Log.Info("Aplicación iniciada (Linux).");
        Opened += OnMainWindowOpened;
        InstanceLock.Listen(() => Dispatcher.UIThread.Post(ShowExistingWindow));
    }

    private void TryCreateTray()
    {
        try
        {
            using var raw = AssetLoader.Open(new Uri("avares://wgrdp/Assets/app-icon.png"));
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
            exit.Click += (_, _) => Dispatcher.UIThread.Post(() => _ = RequestExitAsync());

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
            LoadProfileIntoUi(GetSelectedProfile());
        if (!SetupState.Load().FirstRunCompleted)
            await OpenWizardAsync(createNew: false);
    }

    private async Task OpenWizardAsync(bool createNew)
    {
        Log.Info(createNew
            ? "Abriendo asistente para nuevo perfil."
            : "Abriendo asistente de configuración.");
        var wizard = new FirstRunWizard(createNew ? null : _selectedProfileId, createNew);
        var completed = await wizard.ShowDialog<bool>(this);
        if (!completed)
            return;

        _store = ProfileStore.Load();
        var target = _store.FindById(wizard.SavedProfileId) ?? _store.Profiles[0];
        RefreshProfilePicker(target.Id);
        LoadProfileIntoUi(target);
        SetStatus(createNew ? $"Perfil '{target.Name}' creado." : $"Perfil '{target.Name}' actualizado.", StatusKind.Ready);
    }

    private async void OnWizard(object? sender, RoutedEventArgs e) => await OpenWizardAsync(createNew: false);

    private async void OnWizardNew(object? sender, RoutedEventArgs e) => await OpenWizardAsync(createNew: true);

    private Profile GetSelectedProfile() =>
        _store.FindById(_selectedProfileId) ?? _store.Profiles[0];

    private void RefreshProfilePicker(string? selectProfileId)
    {
        _suppressProfileEvents = true;
        ProfilePicker.Items.Clear();
        var selectedIndex = 0;
        for (var i = 0; i < _store.Profiles.Count; i++)
        {
            var profile = _store.Profiles[i];
            ProfilePicker.Items.Add(profile.Name);
            if (!string.IsNullOrWhiteSpace(selectProfileId)
                && string.Equals(profile.Id, selectProfileId, StringComparison.OrdinalIgnoreCase))
            {
                selectedIndex = i;
            }
        }

        ProfilePicker.SelectedIndex = Math.Min(selectedIndex, _store.Profiles.Count - 1);
        _suppressProfileEvents = false;
    }

    private void OnProfileSelected(object? sender, SelectionChangedEventArgs e)
    {
        if (_suppressProfileEvents)
            return;
        var index = ProfilePicker.SelectedIndex;
        if (index < 0 || index >= _store.Profiles.Count)
            return;
        LoadProfileIntoUi(_store.Profiles[index]);
    }

    private void LoadProfileIntoUi(Profile profile)
    {
        _selectedProfileId = profile.EnsureId();
        ProfileNameBox.Text = profile.Name;
        RdpHostBox.Text = profile.RdpHost;
        RdpPortBox.Text = profile.RdpPort.ToString();
        UserBox.Text = profile.Username;
        DomainBox.Text = profile.Domain;
        ConfStatusBox.Text = profile.DisplayConfLabel;
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

        UpdateConnectionButtons();
    }

    private bool TrySaveSelectedProfile(out Profile? savedProfile, out string? error)
    {
        savedProfile = null;
        error = null;

        var profile = GetSelectedProfile();
        var name = ProfileNameBox.Text?.Trim() ?? "";
        if (name.Length == 0)
        {
            error = "El nombre del perfil no puede estar vacío.";
            return false;
        }

        if (_store.FindByNameExcluding(name, profile.Id) is not null)
        {
            error = $"Ya existe otro perfil llamado '{name}'.";
            return false;
        }

        var tunnelName = string.IsNullOrWhiteSpace(TunnelNameBox.Text) ? "wg-rdp" : TunnelNameBox.Text.Trim();
        try
        {
            TunnelManager.ValidateName(tunnelName);
        }
        catch (Exception ex)
        {
            error = ex.Message;
            return false;
        }

        var previousName = profile.Name;
        profile.Name = name;
        profile.RdpHost = RdpHostBox.Text?.Trim() ?? "";
        profile.Username = UserBox.Text?.Trim() ?? "";
        profile.TunnelName = tunnelName;
        profile.Domain = DomainBox.Text?.Trim() ?? "";
        profile.ServerVpnIp = ServerVpnIpBox.Text?.Trim() ?? "";
        profile.RemoveTunnelOnClose = RemoveTunnelCheck.IsChecked == true;
        profile.RedirectPrinters = RedirectPrintersCheck.IsChecked == true;
        profile.RedirectDrives = RedirectDrivesCheck.IsChecked == true;
        profile.RedirectClipboard = RedirectClipboardCheck.IsChecked == true;
        if (!int.TryParse(RdpPortBox.Text?.Trim(), out var port) || port is < 1 or > 65535)
            port = 3389;
        profile.RdpPort = port;

        _store.Save();

        var enteredPassword = PasswordBox.Text ?? "";
        if (!string.Equals(previousName, profile.Name, StringComparison.OrdinalIgnoreCase))
        {
            var passwordToKeep = enteredPassword.Length > 0
                ? enteredPassword
                : (SecretStore.LoadPassword(previousName) ?? "");
            SecretStore.DeletePassword(previousName);
            if (passwordToKeep.Length > 0)
                SecretStore.SavePassword(profile.Name, passwordToKeep);
        }
        else if (enteredPassword.Length > 0)
        {
            SecretStore.SavePassword(profile.Name, enteredPassword);
        }

        RefreshProfilePicker(profile.Id);
        ConfStatusBox.Text = profile.DisplayConfLabel;
        savedProfile = profile;
        return true;
    }

    private void OnNewProfile(object? sender, RoutedEventArgs e)
    {
        if (_switching)
            return;

        var profile = _store.CreateNew("Nuevo perfil");
        _store.Save();
        RefreshProfilePicker(profile.Id);
        LoadProfileIntoUi(profile);
        ProfileNameBox.Focus();
        ProfileNameBox.SelectAll();
        SetStatus($"Perfil '{profile.Name}' creado. Complete sus datos y el archivo .conf.", StatusKind.Ready);
        Log.Info($"Nuevo perfil '{profile.Name}' creado.");
    }

    private async void OnDeleteProfile(object? sender, RoutedEventArgs e)
    {
        if (_switching)
            return;

        var profile = GetSelectedProfile();
        var isActive = _pipeline is not null
            && string.Equals(_activeProfile?.Id, profile.Id, StringComparison.OrdinalIgnoreCase);

        var prompt = isActive
            ? $"El perfil '{profile.Name}' tiene una conexión activa.\n\n¿Cerrar la sesión RDP y su túnel WireGuard, y eliminar el perfil?"
            : $"¿Eliminar el perfil '{profile.Name}' junto con su configuración WireGuard y contraseña guardada?";

        var confirmed = await Dialogs.ConfirmAsync(this, "Eliminar perfil", prompt);
        if (!confirmed)
            return;

        if (isActive)
            await DisconnectActivePipelineAsync();

        try
        {
            SecretStore.DeletePassword(profile.Name);
            ProfileStore.DeleteManagedConf(profile);
            _store.Profiles.Remove(profile);

            if (_store.Profiles.Count == 0)
            {
                var fallback = new Profile
                {
                    Id = Guid.NewGuid().ToString("N"),
                    Name = "Predeterminado",
                };
                _store.Profiles.Add(fallback);
            }

            _store.Save();
            var next = _store.Profiles[0];
            RefreshProfilePicker(next.Id);
            LoadProfileIntoUi(next);
            SetStatus($"Perfil '{profile.Name}' eliminado.", StatusKind.Ready);
            Log.Info($"Perfil '{profile.Name}' eliminado.");
        }
        catch (Exception ex)
        {
            Log.Error("No se pudo eliminar el perfil", ex);
            SetStatus("Error al eliminar: " + ex.Message, StatusKind.Failed);
            await Dialogs.ShowMessageAsync(this, "Error al eliminar", ex.Message);
        }
    }

    private async void OnBrowseProfileConf(object? sender, RoutedEventArgs e)
    {
        var picked = await Dialogs.PickWireGuardConfAsync(this);
        if (picked is null)
            return;

        try
        {
            var profile = GetSelectedProfile();
            ProfileStore.SaveProfileConf(profile, picked.Content, picked.Label);
            _store.Save();
            ConfStatusBox.Text = profile.DisplayConfLabel;
            SetStatus($"Configuración '{picked.Label}' asociada a '{profile.Name}'.", StatusKind.Ready);
            Log.Info($"Archivo WireGuard '{picked.Label}' importado para el perfil '{profile.Name}'.");
        }
        catch (Exception ex)
        {
            Log.Error("No se pudo guardar la configuración WireGuard del perfil", ex);
            await Dialogs.ShowMessageAsync(this, "Error", ex.Message);
        }
    }

    private async void OnPasteProfileConf(object? sender, RoutedEventArgs e)
    {
        var profile = GetSelectedProfile();
        string? existing = null;
        if (File.Exists(profile.ResolvedTunnelConfPath))
        {
            try { existing = await File.ReadAllTextAsync(profile.ResolvedTunnelConfPath); }
            catch { /* opcional */ }
        }

        var pasted = await Dialogs.PromptPasteWireGuardConfAsync(this, existing);
        if (pasted is null)
            return;

        try
        {
            ProfileStore.SaveProfileConf(profile, pasted, "Configuración pegada");
            _store.Save();
            ConfStatusBox.Text = profile.DisplayConfLabel;
            SetStatus($"Configuración pegada guardada para '{profile.Name}'.", StatusKind.Ready);
            Log.Info($"Configuración WireGuard pegada y guardada para el perfil '{profile.Name}'.");
        }
        catch (Exception ex)
        {
            Log.Error("No se pudo guardar la configuración pegada", ex);
            await Dialogs.ShowMessageAsync(this, "Error", ex.Message);
        }
    }

    private async void OnSaveProfile(object? sender, RoutedEventArgs e)
    {
        try
        {
            if (!TrySaveSelectedProfile(out var profile, out var error) || profile is null)
            {
                SetStatus(error ?? "No se pudo guardar el perfil.", StatusKind.Failed);
                await Dialogs.ShowMessageAsync(this, "No se pudo guardar", error ?? "Revise los campos.");
                return;
            }

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

    private async void OnConnect(object? sender, RoutedEventArgs e)
    {
        RestoreFromTray();
        if (_switching)
            return;

        Profile? profile;
        string? saveError;
        try
        {
            if (!TrySaveSelectedProfile(out profile, out saveError) || profile is null)
            {
                SetStatus(saveError ?? "Revise los datos del perfil.", StatusKind.Failed);
                await Dialogs.ShowMessageAsync(this, "Perfil incompleto", saveError ?? "Revise los datos del perfil.");
                return;
            }
        }
        catch (Exception ex)
        {
            Log.Error("No se pudo guardar el perfil antes de conectar", ex);
            SetStatus("Error: " + ex.Message, StatusKind.Failed);
            await Dialogs.ShowMessageAsync(this, "Error", ex.Message);
            return;
        }

        if (string.IsNullOrWhiteSpace(profile.RdpHost) || string.IsNullOrWhiteSpace(profile.Username))
        {
            SetStatus("Faltan el servidor o el usuario.", StatusKind.Failed);
            return;
        }

        if (!File.Exists(profile.ResolvedTunnelConfPath))
        {
            SetStatus("Falta el archivo .conf de WireGuard para este perfil.", StatusKind.Failed);
            await Dialogs.ShowMessageAsync(this, "Falta configuración WireGuard",
                $"El perfil '{profile.Name}' no tiene un archivo .conf configurado. Use «Examinar…» o «Pegar .conf…».");
            return;
        }

        var password = PasswordBox.Text ?? "";
        if (password.Length == 0)
            password = SecretStore.LoadPassword(profile.Name) ?? "";

        if (password.Length == 0)
        {
            SetStatus("Falta la contraseña.", StatusKind.Failed);
            return;
        }

        if (_pipeline is not null)
        {
            if (string.Equals(_activeProfile?.Id, profile.Id, StringComparison.OrdinalIgnoreCase))
                return;

            var switchConfirmed = await Dialogs.ConfirmAsync(
                this,
                "Cambiar de conexión",
                $"Hay una conexión activa con '{_activeProfile?.Name}' (túnel '{_activeProfile?.TunnelName}').\n\n" +
                $"¿Desea cerrar esa sesión y su túnel para conectar con '{profile.Name}'?");
            if (!switchConfirmed)
                return;

            await DisconnectActivePipelineAsync();
        }

        var otherTunnels = _store.Profiles
            .Where(p => !string.Equals(p.Id, profile.Id, StringComparison.OrdinalIgnoreCase))
            .Select(p => p.TunnelName)
            .Where(t => !string.IsNullOrWhiteSpace(t))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        var pipeline = new Pipeline(profile, password, otherTunnels);
        _pipeline = pipeline;
        _activeProfile = pipeline.Profile;
        pipeline.StateChanged += state => OnPipelineState(pipeline, state);
        pipeline.VpnReady += () => OnVpnReady(pipeline, password);

        UpdateConnectionButtons();
        SetStatus($"Iniciando '{profile.Name}'…", StatusKind.Busy);
        _ = Task.Run(() => pipeline.RunAsync());
    }

    private async Task DisconnectActivePipelineAsync()
    {
        var oldPipeline = _pipeline;
        if (oldPipeline is null)
            return;

        _switching = true;
        UpdateConnectionButtons();
        SetStatus("Cerrando conexión anterior…", StatusKind.Busy);

        _pipeline = null;
        _activeProfile = null;
        _rdp?.Disconnect();
        try
        {
            await oldPipeline.ShutdownAsync();
        }
        catch (Exception ex)
        {
            Log.Warn("Error al cerrar la conexión anterior: " + ex.Message);
        }
        finally
        {
            _rdp?.Dispose();
            _rdp = null;
            _switching = false;
            PlaceholderText.Text = "Pulse Conectar para iniciar la sesión. El escritorio se abre en una ventana de Remmina.";
            UpdateConnectionButtons();
        }
    }

    private void OnCancel(object? sender, RoutedEventArgs e)
    {
        _pipeline?.Cancel();
        _rdp?.Disconnect();
    }

    private void OnPipelineState(Pipeline pipeline, PipelineState state)
    {
        Dispatcher.UIThread.Post(() =>
        {
            if (!ReferenceEquals(pipeline, _pipeline))
                return;

            var profileLabel = pipeline.Profile.Name;
            var (text, kind) = state switch
            {
                PipelineState.EnsuringWireGuard => ("Comprobando WireGuard y FreeRDP…", StatusKind.Busy),
                PipelineState.ImportingTunnel => ("Importando túnel…", StatusKind.Busy),
                PipelineState.ConnectingVpn => ($"Conectando VPN ({pipeline.Profile.TunnelName})…", StatusKind.Busy),
                PipelineState.WaitingHandshake => ("Esperando enlace VPN…", StatusKind.Busy),
                PipelineState.ConnectingRdp => ("Abriendo escritorio remoto…", StatusKind.Busy),
                PipelineState.SessionActive => ($"Sesión activa — {profileLabel}", StatusKind.Active),
                PipelineState.TearingDown => ("Cerrando túnel…", StatusKind.Busy),
                PipelineState.Finished => ("Listo", StatusKind.Ready),
                PipelineState.Failed => ("Error: " + (pipeline.LastError ?? "desconocido"), StatusKind.Failed),
                _ => ("Listo", StatusKind.Ready),
            };
            SetStatus(text, kind);
            if (state is PipelineState.Finished or PipelineState.Failed)
                ClearActivePipeline();
        });
    }

    private void OnVpnReady(Pipeline pipeline, string password)
    {
        Dispatcher.UIThread.Post(() =>
        {
            if (!ReferenceEquals(pipeline, _pipeline))
                return;

            var profile = pipeline.Profile;
            var secret = SecretStore.LoadPassword(profile.Name);
            if (string.IsNullOrEmpty(secret))
                secret = password;
            _ = StartFreeRdpAsync(pipeline, profile, secret);
        });
    }

    private async Task StartFreeRdpAsync(Pipeline pipeline, Profile profile, string? password)
    {
        try
        {
            if (string.IsNullOrEmpty(password))
                throw new InvalidOperationException("Falta la contraseña para FreeRDP.");

            PlaceholderText.Text = $"La sesión de '{profile.Name}' está abierta en la ventana de Remmina.";
            var rdp = new FreeRdpSession();
            _rdp = rdp;
            rdp.Ended += failed => Dispatcher.UIThread.Post(() =>
            {
                if (ReferenceEquals(pipeline, _pipeline))
                    pipeline.OnRdpEnded(failed);
            });
            await rdp.StartAsync(profile, password);
            if (ReferenceEquals(pipeline, _pipeline))
                pipeline.OnRdpConnecting();
        }
        catch (Exception ex)
        {
            Log.Error("No se pudo iniciar la sesión RDP", ex);
            if (ReferenceEquals(pipeline, _pipeline))
                pipeline.OnRdpEnded(failed: true);
        }
    }

    private void ClearActivePipeline()
    {
        _pipeline = null;
        _activeProfile = null;
        _rdp?.Dispose();
        _rdp = null;
        PlaceholderText.Text = "Pulse Conectar para iniciar la sesión. El escritorio se abre en una ventana de Remmina.";
        UpdateConnectionButtons();
    }

    private void UpdateConnectionButtons()
    {
        var running = _pipeline is not null;
        var sameAsActive = running
            && string.Equals(_selectedProfileId, _activeProfile?.Id, StringComparison.OrdinalIgnoreCase);

        ConnectButton.Content = running && !sameAsActive ? "Cambiar y conectar" : "Conectar y abrir RDP";
        ConnectButton.IsEnabled = !_switching && (!running || !sameAsActive);
        CancelButton.IsEnabled = running && !_switching;
        SaveButton.IsEnabled = !_switching;
        NewProfileButton.IsEnabled = !_switching;
        DeleteProfileButton.IsEnabled = !_switching;
        WizardEditButton.IsEnabled = !_switching;
        WizardNewButton.IsEnabled = !_switching;

        if (_connectItem is not null)
            _connectItem.IsEnabled = ConnectButton.IsEnabled;
        if (_disconnectItem is not null)
            _disconnectItem.IsEnabled = running;
    }

    protected override void OnClosing(WindowClosingEventArgs e)
    {
        if (!_exitRequested)
        {
            e.Cancel = true;
            if (_trayAvailable)
            {
                Hide();
                Log.Info("La ventana sigue en la bandeja. Use Salir para cerrar del todo.");
                return;
            }

            _ = RequestExitAsync();
            return;
        }

        _lifetime.Cancel();
        _pipeline?.Cancel();
        _rdp?.Dispose();
        RemoveTrayIcon();
        InstanceLock.Release();
        _lifetime.Dispose();
        base.OnClosing(e);
    }

    private void RestoreFromTray()
    {
        if (!IsVisible)
            Show();
        if (WindowState == WindowState.Minimized)
            WindowState = WindowState.Normal;
        Activate();
    }

    private void ShowExistingWindow()
    {
        if (!IsVisible)
            Show();
        WindowState = WindowState.Maximized;
        Activate();
    }

    private async void OnExit(object? sender, RoutedEventArgs e) => await RequestExitAsync();

    private async Task RequestExitAsync()
    {
        if (_exiting || _exitRequested)
            return;

        if (!IsVisible)
            Show();
        if (WindowState == WindowState.Minimized)
            WindowState = WindowState.Normal;
        Activate();

        var leave = await Dialogs.ConfirmAsync(this, "Salir de WG-RDP", "¿Quiere salir de la aplicación?");
        if (!leave)
            return;

        await ExitCompletelyAsync();
    }

    private async Task ExitCompletelyAsync()
    {
        if (_exiting)
            return;
        _exiting = true;
        ExitButton.IsEnabled = false;
        SetStatus("Cerrando túnel…", StatusKind.Busy);

        _rdp?.Disconnect();
        var pipeline = _pipeline;
        try
        {
            if (pipeline is not null)
            {
                await pipeline.ShutdownAsync();
            }
            else
            {
                foreach (var group in _store.Profiles
                    .Where(p => !string.IsNullOrWhiteSpace(p.TunnelName))
                    .GroupBy(p => p.TunnelName.Trim(), StringComparer.OrdinalIgnoreCase))
                {
                    var tunnelName = group.Key;
                    var removeConf = group.Any(p => p.RemoveTunnelOnClose);
                    if (await TunnelManager.IsUpAsync(tunnelName))
                        await TunnelManager.DownAsync(tunnelName, removeConf, CancellationToken.None);
                }
            }
        }
        catch (Exception ex)
        {
            Log.Error("No se pudo cerrar el túnel al salir", ex);
        }

        _exitRequested = true;
        Close();
    }

    private void RemoveTrayIcon()
    {
        if (_tray is null)
            return;
        _tray.IsVisible = false;
        if (Application.Current is not null)
            TrayIcon.SetIcons(Application.Current, new TrayIcons());
        _tray.Dispose();
        _tray = null;
        _trayAvailable = false;
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
