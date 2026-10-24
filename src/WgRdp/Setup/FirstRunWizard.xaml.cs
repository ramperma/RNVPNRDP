using System.Windows;
using System.Windows.Controls;
using Microsoft.Win32;
using WgRdp.Elevation;
using WgRdp.Logging;
using WgRdp.Profiles;
using WgRdp.WireGuard;

namespace WgRdp.Setup;

/// <summary>
/// Protocolo de instalación inicial (3 pasos):
///   1) Descargar e instalar WireGuard si falta.
///   2) Aportar la configuración del túnel (.conf por archivo o pegada).
///   3) Introducir los datos del usuario RDP.
/// </summary>
public partial class FirstRunWizard : Window
{
    private const string DefaultConfFileName = "default.conf";

    private static string DefaultConfPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
        "WgRdp", DefaultConfFileName);

    private int _step = 1;
    private bool _wgReady;
    private bool _confReady;

    public FirstRunWizard()
    {
        InitializeComponent();
        UpdateStepUi();
        _ = CheckWireGuardAsync();
    }

    // ---------- Paso 1: WireGuard ----------

    private async Task CheckWireGuardAsync()
    {
        await Task.Yield();
        Dispatcher.Invoke(() =>
        {
            if (WireGuardInstaller.IsWgCliInstalled())
            {
                _wgReady = true;
                WgStatusText.Text = "WireGuard ya está instalado ✔";
                InstallWgButton.IsEnabled = false;
                _ = WireGuardUi.StopUserInterfaceAsync();
            }
            else
            {
                _wgReady = false;
                WgStatusText.Text = "WireGuard no está instalado.";
                InstallWgButton.IsEnabled = true;
            }
        });
    }

    private async void OnInstallWireGuard(object sender, RoutedEventArgs e)
    {
        InstallWgButton.IsEnabled = false;
        DownloadProgress.Visibility = Visibility.Visible;
        DownloadText.Text = "Descargando instalador oficial…";

        try
        {
            var progress = new Progress<(long Done, long Total)>(p =>
            {
                if (p.Total > 0)
                {
                    DownloadProgress.Maximum = p.Total;
                    DownloadProgress.Value = p.Done;
                    DownloadText.Text =
                        $"Descargando… {p.Done / 1024} KB de {p.Total / 1024} KB";
                }
                else
                {
                    DownloadText.Text = $"Descargando… {p.Done / 1024} KB";
                }
            });

            await WireGuardInstaller.DownloadLatestInstallerAsync(progress);

            DownloadText.Text = "Instalando (espere el aviso de UAC)…";
            var code = await ElevatedRunner.RunElevatedAsync("install-wireguard");

            if (code == 0 && WireGuardInstaller.IsWgCliInstalled())
            {
                _wgReady = true;
                WgStatusText.Text = "WireGuard instalado correctamente ✔";
                DownloadText.Text = "";
                Log.Info("Paso 1 completado: WireGuard instalado.");
                await WireGuardUi.StopUserInterfaceAsync();
            }
            else if (code == 1223)
            {
                WgStatusText.Text = "Instalación cancelada en el aviso de UAC.";
                InstallWgButton.IsEnabled = true;
            }
            else
            {
                WgStatusText.Text = $"La instalación falló (código {code}). Reintente.";
                InstallWgButton.IsEnabled = true;
            }
        }
        catch (Exception ex)
        {
            Log.Error("Fallo en la descarga/instalación de WireGuard", ex);
            WgStatusText.Text = "Error al descargar: " + ex.Message;
            DownloadText.Text = "";
            InstallWgButton.IsEnabled = true;
            MessageBox.Show(this, ex.Message, "Descarga de WireGuard",
                MessageBoxButton.OK, MessageBoxImage.Error);
        }
        finally
        {
            DownloadProgress.Visibility = Visibility.Collapsed;
        }
    }

    // ---------- Paso 2: configuración del túnel ----------

    private void OnBrowseConf(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog
        {
            Title = "Seleccione la configuración del túnel WireGuard",
            Filter = "Configuración WireGuard (*.conf)|*.conf|Todos los archivos (*.*)|*.*",
        };
        if (dialog.ShowDialog(this) != true)
            return;

        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(DefaultConfPath)!);
            File.Copy(dialog.FileName, DefaultConfPath, overwrite: true);
            ConfPathBox.Text = DefaultConfPath;
            _confReady = true;
            Log.Info($"Configuración del túnel copiada desde {dialog.FileName}");
        }
        catch (Exception ex)
        {
            Log.Error("No se pudo copiar la configuración", ex);
            MessageBox.Show(this, ex.Message, "Error", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private bool AcceptPastedConf()
    {
        var text = ConfPasteBox.Text.Trim();
        if (text.Length == 0)
            return false;

        if (!text.Contains("[Interface]", StringComparison.OrdinalIgnoreCase) ||
            !text.Contains("[Peer]", StringComparison.OrdinalIgnoreCase))
        {
            MessageBox.Show(this,
                "El texto pegado no parece una configuración WireGuard (falta [Interface] o [Peer]).",
                "Configuración inválida", MessageBoxButton.OK, MessageBoxImage.Warning);
            return false;
        }

        Directory.CreateDirectory(Path.GetDirectoryName(DefaultConfPath)!);
        File.WriteAllText(DefaultConfPath, text);
        ConfPathBox.Text = DefaultConfPath;
        _confReady = true;
        Log.Info("Configuración del túnel guardada desde el texto pegado.");
        return true;
    }

    // ---------- Navegación ----------

    private void OnNext(object sender, RoutedEventArgs e)
    {
        if (_step == 1 && !_wgReady)
        {
            MessageBox.Show(this, "Instale primero el cliente WireGuard.", "Paso 1",
                MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        if (_step == 2 && !_confReady && !AcceptPastedConf())
            return;

        _step = Math.Min(_step + 1, 3);
        UpdateStepUi();
    }

    private void OnBack(object sender, RoutedEventArgs e)
    {
        _step = Math.Max(_step - 1, 1);
        UpdateStepUi();
    }

    private void UpdateStepUi()
    {
        Step1Panel.Visibility = _step == 1 ? Visibility.Visible : Visibility.Collapsed;
        Step2Panel.Visibility = _step == 2 ? Visibility.Visible : Visibility.Collapsed;
        Step3Panel.Visibility = _step == 3 ? Visibility.Visible : Visibility.Collapsed;

        StepTitle.Text = _step switch
        {
            1 => "Paso 1 — Instalar WireGuard",
            2 => "Paso 2 — Configuración del túnel",
            _ => "Paso 3 — Datos del usuario RDP",
        };
        StepIndicator.Text = $"Paso {_step} de 3";
        BackButton.IsEnabled = _step > 1;
        NextButton.Visibility = _step < 3 ? Visibility.Visible : Visibility.Collapsed;
        FinishButton.Visibility = _step == 3 ? Visibility.Visible : Visibility.Collapsed;
    }

    // ---------- Finalizar ----------

    private void OnFinish(object sender, RoutedEventArgs e)
    {
        var host = RdpHostBox.Text.Trim();
        var user = UserBox.Text.Trim();
        if (host.Length == 0 || user.Length == 0)
        {
            MessageBox.Show(this, "El servidor RDP y el usuario son obligatorios.", "Paso 3",
                MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        if (!_confReady && !File.Exists(DefaultConfPath) && !AcceptPastedConf())
        {
            MessageBox.Show(this, "Falta la configuración del túnel (paso 2).", "Paso 2",
                MessageBoxButton.OK, MessageBoxImage.Information);
            _step = 2;
            UpdateStepUi();
            return;
        }

        var store = ProfileStore.Load();
        var profile = store.Profiles[0];

        profile.Name = "Predeterminado";
        profile.RdpHost = host;
        profile.Username = user;
        profile.Domain = DomainBox.Text.Trim();
        profile.TunnelName = string.IsNullOrWhiteSpace(TunnelNameBox.Text)
            ? "wg-rdp" : TunnelNameBox.Text.Trim();
        profile.ServerVpnIp = ServerVpnIpBox.Text.Trim();
        profile.TunnelConfPath = DefaultConfPath;
        if (!int.TryParse(RdpPortBox.Text.Trim(), out var port) || port is < 1 or > 65535)
            port = 3389;
        profile.RdpPort = port;
        profile.RedirectPrinters = RedirectPrintersCheck.IsChecked == true;
        profile.RedirectDrives = RedirectDrivesCheck.IsChecked == true;
        profile.RedirectClipboard = RedirectClipboardCheck.IsChecked == true;

        store.Save();

        if (PasswordBox.Password.Length > 0)
            SecretStore.SavePassword(profile.Name, PasswordBox.Password);

        var setup = SetupState.Load();
        setup.FirstRunCompleted = true;
        setup.Save();

        Log.Info("Configuración inicial completada.");
        DialogResult = true;
    }
}
