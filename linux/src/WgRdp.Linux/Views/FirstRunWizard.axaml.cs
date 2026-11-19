using Avalonia.Controls;
using Avalonia.Interactivity;
using WgRdp.Linux;
using WgRdp.Logging;
using WgRdp.Profiles;
using WgRdp.Setup;

namespace WgRdp.Views;

public partial class FirstRunWizard : Window
{
    private readonly bool _createNew;
    private readonly string? _targetProfileId;
    private int _step = 1;
    private bool _toolsReady;
    private bool _hasExistingConf;
    private string? _pendingConfText;
    private string? _pendingConfLabel;

    public string? SavedProfileId { get; private set; }

    public FirstRunWizard()
        : this(targetProfileId: null, createNew: false)
    {
    }

    public FirstRunWizard(string? targetProfileId, bool createNew)
    {
        _targetProfileId = targetProfileId;
        _createNew = createNew;
        InitializeComponent();
        Title = createNew ? "Nuevo perfil — WG-RDP" : "Asistente de configuración — WG-RDP";
        Opened += (_, _) => _ = RefreshToolsAsync();

        var store = ProfileStore.Load();
        if (createNew)
        {
            ProfileNameBox.Text = store.UniqueName("Nuevo perfil");
            RdpPortBox.Text = "3389";
            TunnelNameBox.Text = "wg-rdp";
            RedirectPrintersCheck.IsChecked = true;
            RedirectDrivesCheck.IsChecked = true;
            RedirectClipboardCheck.IsChecked = true;
        }
        else
        {
            var profile = store.FindById(targetProfileId) ?? store.Profiles[0];
            _targetProfileId = profile.Id;

            if (profile.HasExplicitOrExistingConf)
            {
                _hasExistingConf = true;
                ConfPathBox.Text = profile.DisplayConfLabel;
                ConfStatusText.Text = "Configuración actual lista (puede reemplazarla seleccionando otro archivo).";
            }

            ProfileNameBox.Text = profile.Name;
            RdpHostBox.Text = profile.RdpHost;
            RdpPortBox.Text = profile.RdpPort.ToString();
            UserBox.Text = profile.Username;
            DomainBox.Text = profile.Domain;
            TunnelNameBox.Text = string.IsNullOrWhiteSpace(profile.TunnelName) ? "wg-rdp" : profile.TunnelName;
            ServerVpnIpBox.Text = profile.ServerVpnIp;
            RedirectPrintersCheck.IsChecked = profile.RedirectPrinters;
            RedirectDrivesCheck.IsChecked = profile.RedirectDrives;
            RedirectClipboardCheck.IsChecked = profile.RedirectClipboard;
            PasswordBox.Text = SecretStore.LoadPassword(profile.Name) ?? "";
        }

        UpdateStepUi();
    }

    private async Task RefreshToolsAsync()
    {
        var status = LinuxTools.Detect();
        _toolsReady = status.WireGuard && status.FreeRdp;
        WgStatusText.Text = status.Describe();
        InstallButton.IsEnabled = !status.WireGuard || !status.FreeRdp || !status.SecretTool;
        InstallText.Text = "";
        await Task.CompletedTask;
    }

    private async void OnInstall(object? sender, RoutedEventArgs e)
    {
        InstallButton.IsEnabled = false;
        InstallText.Text = "Instalando… espere el diálogo de autorización. Puede tardar varios minutos.";
        try
        {
            var status = LinuxTools.Detect();
            var result = await LinuxPackages.InstallMissingAsync(status);
            if (result.ExitCode is RootRunner.Dismissed or RootRunner.Denied)
            {
                WgStatusText.Text = "Autorización cancelada o denegada.";
                InstallButton.IsEnabled = true;
                InstallText.Text = "";
                return;
            }

            if (!result.Success)
            {
                WgStatusText.Text = "La instalación falló. Revise el registro de la ventana principal.";
                InstallButton.IsEnabled = true;
                InstallText.Text = "";
                await Dialogs.ShowMessageAsync(this, "Instalación",
                    "No se pudieron instalar los paquetes. Instale a mano wireguard-tools y freerdp3-x11.");
                return;
            }

            await RefreshToolsAsync();
            InstallText.Text = _toolsReady ? "Listo." : "";
        }
        catch (Exception ex)
        {
            Log.Error("Fallo al instalar componentes", ex);
            WgStatusText.Text = ex.Message;
            InstallButton.IsEnabled = true;
            InstallText.Text = "";
            await Dialogs.ShowMessageAsync(this, "Instalación", ex.Message);
        }
    }

    private async void OnBrowseConf(object? sender, RoutedEventArgs e)
    {
        var picked = await Dialogs.PickWireGuardConfAsync(this);
        if (picked is null)
            return;

        _pendingConfText = picked.Content;
        _pendingConfLabel = picked.Label;
        ConfPathBox.Text = string.IsNullOrWhiteSpace(picked.SourcePath)
            ? picked.Label
            : $"{picked.Label} ({picked.SourcePath})";
        ConfStatusText.Text = $"Archivo válido seleccionado: {picked.Label}";
        ConfPasteBox.Text = "";
        Log.Info($"Configuración WireGuard seleccionada en el asistente: {picked.Label}");
    }

    private bool CapturePastedConfIfAny()
    {
        var text = ConfPasteBox.Text?.Trim() ?? "";
        if (text.Length == 0)
            return _pendingConfText is not null || _hasExistingConf;

        if (!ProfileStore.LooksLikeWireGuard(text))
            return false;

        _pendingConfText = text;
        _pendingConfLabel = "Configuración pegada";
        ConfPathBox.Text = _pendingConfLabel;
        ConfStatusText.Text = "Configuración pegada válida.";
        return true;
    }

    private async void OnNext(object? sender, RoutedEventArgs e)
    {
        if (_step == 1 && !_toolsReady)
        {
            await Dialogs.ShowMessageAsync(this, "Paso 1", "Instale primero WireGuard y FreeRDP.");
            return;
        }

        if (_step == 2 && !CapturePastedConfIfAny())
        {
            await Dialogs.ShowMessageAsync(this, "Configuración inválida",
                "Elija un archivo .conf o pegue una configuración con [Interface] y [Peer].");
            return;
        }

        _step = Math.Min(_step + 1, 3);
        UpdateStepUi();
    }

    private void OnBack(object? sender, RoutedEventArgs e)
    {
        _step = Math.Max(_step - 1, 1);
        UpdateStepUi();
    }

    private void UpdateStepUi()
    {
        Step1Panel.IsVisible = _step == 1;
        Step2Panel.IsVisible = _step == 2;
        Step3Panel.IsVisible = _step == 3;
        StepTitle.Text = _step switch
        {
            1 => "Paso 1 — Comprobar componentes",
            2 => "Paso 2 — Configuración del túnel",
            _ => "Paso 3 — Datos del perfil y usuario RDP",
        };
        StepIndicator.Text = $"Paso {_step} de 3";
        BackButton.IsEnabled = _step > 1;
        NextButton.IsVisible = _step < 3;
        FinishButton.IsVisible = _step == 3;
    }

    private async void OnFinish(object? sender, RoutedEventArgs e)
    {
        var profileName = ProfileNameBox.Text?.Trim() ?? "";
        var host = RdpHostBox.Text?.Trim() ?? "";
        var user = UserBox.Text?.Trim() ?? "";
        var tunnelName = string.IsNullOrWhiteSpace(TunnelNameBox.Text) ? "wg-rdp" : TunnelNameBox.Text.Trim();

        if (profileName.Length == 0)
        {
            await Dialogs.ShowMessageAsync(this, "Paso 3", "Indique un nombre para el perfil.");
            return;
        }

        if (host.Length == 0 || user.Length == 0)
        {
            await Dialogs.ShowMessageAsync(this, "Paso 3", "El servidor RDP y el usuario son obligatorios.");
            return;
        }

        try
        {
            TunnelManager.ValidateName(tunnelName);
        }
        catch (Exception ex)
        {
            await Dialogs.ShowMessageAsync(this, "Nombre de túnel inválido", ex.Message);
            return;
        }

        if (!CapturePastedConfIfAny())
        {
            await Dialogs.ShowMessageAsync(this, "Paso 2", "Falta la configuración del túnel (.conf).");
            _step = 2;
            UpdateStepUi();
            return;
        }

        try
        {
            var store = ProfileStore.Load();
            Profile profile;
            string? previousName = null;

            if (_createNew)
            {
                if (store.Find(profileName) is not null)
                {
                    await Dialogs.ShowMessageAsync(this, "Nombre duplicado",
                        $"Ya existe un perfil llamado '{profileName}'. Elija otro nombre.");
                    return;
                }

                profile = new Profile
                {
                    Id = Guid.NewGuid().ToString("N"),
                    Name = profileName,
                };
                store.Profiles.Add(profile);
            }
            else
            {
                profile = store.FindById(_targetProfileId) ?? store.Profiles[0];
                if (store.FindByNameExcluding(profileName, profile.Id) is not null)
                {
                    await Dialogs.ShowMessageAsync(this, "Nombre duplicado",
                        $"Ya existe otro perfil llamado '{profileName}'. Elija otro nombre.");
                    return;
                }

                previousName = profile.Name;
                profile.Name = profileName;
            }

            if (_pendingConfText is not null)
                ProfileStore.SaveProfileConf(profile, _pendingConfText, _pendingConfLabel ?? "wireguard.conf");

            profile.RdpHost = host;
            profile.Username = user;
            profile.Domain = DomainBox.Text?.Trim() ?? "";
            profile.TunnelName = tunnelName;
            profile.ServerVpnIp = ServerVpnIpBox.Text?.Trim() ?? "";
            if (!int.TryParse(RdpPortBox.Text?.Trim(), out var port) || port is < 1 or > 65535)
                port = 3389;
            profile.RdpPort = port;
            profile.RedirectPrinters = RedirectPrintersCheck.IsChecked == true;
            profile.RedirectDrives = RedirectDrivesCheck.IsChecked == true;
            profile.RedirectClipboard = RedirectClipboardCheck.IsChecked == true;
            store.Save();

            var newPassword = PasswordBox.Text ?? "";
            if (!string.IsNullOrEmpty(previousName)
                && !string.Equals(previousName, profile.Name, StringComparison.OrdinalIgnoreCase))
            {
                if (newPassword.Length == 0)
                    newPassword = SecretStore.LoadPassword(previousName) ?? "";
                SecretStore.DeletePassword(previousName);
            }

            if (newPassword.Length > 0)
                SecretStore.SavePassword(profile.Name, newPassword);

            var setup = SetupState.Load();
            setup.FirstRunCompleted = true;
            setup.Save();

            SavedProfileId = profile.Id;
            Log.Info(_createNew
                ? $"Perfil '{profile.Name}' creado desde el asistente."
                : $"Perfil '{profile.Name}' actualizado desde el asistente.");
            Close(true);
        }
        catch (Exception ex)
        {
            Log.Error("Error al guardar el perfil desde el asistente", ex);
            await Dialogs.ShowMessageAsync(this, "Error al guardar", ex.Message);
        }
    }
}
