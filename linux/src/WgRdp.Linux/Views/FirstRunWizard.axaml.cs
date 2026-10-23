using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using WgRdp.Linux;
using WgRdp.Logging;
using WgRdp.Paths;
using WgRdp.Profiles;
using WgRdp.Setup;

namespace WgRdp.Views;

public partial class FirstRunWizard : Window
{
    private int _step = 1;
    private bool _toolsReady;
    private bool _confReady;

    public FirstRunWizard()
    {
        InitializeComponent();
        Opened += (_, _) => _ = RefreshToolsAsync();
        if (File.Exists(AppPaths.DefaultConfPath))
        {
            ConfPathBox.Text = AppPaths.DefaultConfPath;
            _confReady = true;
        }

        var profile = ProfileStore.Load().Profiles[0];
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
        var files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "Seleccione la configuración del túnel WireGuard",
            AllowMultiple = false,
            FileTypeFilter =
            [
                new FilePickerFileType("Configuración WireGuard") { Patterns = ["*.conf"] },
                new FilePickerFileType("Todos los archivos") { Patterns = ["*"] },
            ],
        });
        if (files.Count == 0)
            return;

        try
        {
            var picked = files[0];
            AppPaths.Ensure();
            var local = picked.TryGetLocalPath();
            if (!string.IsNullOrEmpty(local))
                File.Copy(local, AppPaths.DefaultConfPath, overwrite: true);
            else
            {
                await using var stream = await picked.OpenReadAsync();
                await using var dest = File.Create(AppPaths.DefaultConfPath);
                await stream.CopyToAsync(dest);
            }

            AppPaths.TryPrivate(AppPaths.DefaultConfPath);
            if (!LooksLikeWireGuard(File.ReadAllText(AppPaths.DefaultConfPath)))
            {
                await Dialogs.ShowMessageAsync(this, "Configuración inválida",
                    "El archivo no parece una configuración WireGuard (falta [Interface] o [Peer]).");
                return;
            }

            ConfPathBox.Text = AppPaths.DefaultConfPath;
            _confReady = true;
            Log.Info("Configuración del túnel copiada.");
        }
        catch (Exception ex)
        {
            Log.Error("No se pudo copiar la configuración", ex);
            await Dialogs.ShowMessageAsync(this, "Error", ex.Message);
        }
    }

    private bool AcceptPastedConf()
    {
        var text = ConfPasteBox.Text?.Trim() ?? "";
        if (text.Length == 0)
            return _confReady;
        if (!LooksLikeWireGuard(text))
            return false;

        AppPaths.Ensure();
        File.WriteAllText(AppPaths.DefaultConfPath, text + Environment.NewLine);
        AppPaths.TryPrivate(AppPaths.DefaultConfPath);
        ConfPathBox.Text = AppPaths.DefaultConfPath;
        _confReady = true;
        Log.Info("Configuración del túnel guardada desde el texto pegado.");
        return true;
    }

    private async void OnNext(object? sender, RoutedEventArgs e)
    {
        if (_step == 1 && !_toolsReady)
        {
            await Dialogs.ShowMessageAsync(this, "Paso 1", "Instale primero WireGuard y FreeRDP.");
            return;
        }

        if (_step == 2 && !AcceptPastedConf())
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
            1 => "Paso 1 — Instalar componentes",
            2 => "Paso 2 — Configuración del túnel",
            _ => "Paso 3 — Datos del usuario RDP",
        };
        StepIndicator.Text = $"Paso {_step} de 3";
        BackButton.IsEnabled = _step > 1;
        NextButton.IsVisible = _step < 3;
        FinishButton.IsVisible = _step == 3;
    }

    private async void OnFinish(object? sender, RoutedEventArgs e)
    {
        var host = RdpHostBox.Text?.Trim() ?? "";
        var user = UserBox.Text?.Trim() ?? "";
        if (host.Length == 0 || user.Length == 0)
        {
            await Dialogs.ShowMessageAsync(this, "Paso 3", "El servidor RDP y el usuario son obligatorios.");
            return;
        }

        if (!_confReady && !File.Exists(AppPaths.DefaultConfPath) && !AcceptPastedConf())
        {
            await Dialogs.ShowMessageAsync(this, "Paso 2", "Falta la configuración del túnel.");
            _step = 2;
            UpdateStepUi();
            return;
        }

        var store = ProfileStore.Load();
        var profile = store.Profiles[0];
        profile.Name = string.IsNullOrWhiteSpace(profile.Name) ? "Predeterminado" : profile.Name;
        profile.RdpHost = host;
        profile.Username = user;
        profile.Domain = DomainBox.Text?.Trim() ?? "";
        profile.TunnelName = string.IsNullOrWhiteSpace(TunnelNameBox.Text) ? "wg-rdp" : TunnelNameBox.Text.Trim();
        profile.ServerVpnIp = ServerVpnIpBox.Text?.Trim() ?? "";
        profile.TunnelConfPath = AppPaths.DefaultConfPath;
        if (!int.TryParse(RdpPortBox.Text?.Trim(), out var port) || port is < 1 or > 65535)
            port = 3389;
        profile.RdpPort = port;
        profile.RedirectPrinters = RedirectPrintersCheck.IsChecked == true;
        profile.RedirectDrives = RedirectDrivesCheck.IsChecked == true;
        profile.RedirectClipboard = RedirectClipboardCheck.IsChecked == true;
        store.Save();

        if ((PasswordBox.Text?.Length ?? 0) > 0)
            SecretStore.SavePassword(profile.Name, PasswordBox.Text!);

        var setup = SetupState.Load();
        setup.FirstRunCompleted = true;
        setup.Save();
        Log.Info("Configuración inicial completada.");
        Close(true);
    }

    private static bool LooksLikeWireGuard(string text) =>
        text.Contains("[Interface]", StringComparison.OrdinalIgnoreCase)
        && text.Contains("[Peer]", StringComparison.OrdinalIgnoreCase);
}
