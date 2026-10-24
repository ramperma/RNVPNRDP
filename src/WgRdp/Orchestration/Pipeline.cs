using WgRdp.Elevation;
using WgRdp.Logging;
using WgRdp.Profiles;
using WgRdp.WireGuard;

namespace WgRdp.Orchestration;

public enum PipelineState
{
    Idle,
    EnsuringWireGuard,
    ImportingTunnel,
    ConnectingVpn,
    WaitingHandshake,
    ConnectingRdp,
    SessionActive,
    TearingDown,
    Finished,
    Failed,
}

/// <summary>
/// Máquina de estados: Install → Import → Connect VPN → Health → RDP → Teardown.
/// Los pasos que requieren admin se delegan a una instancia elevada (runas).
/// </summary>
public sealed class Pipeline
{
    private readonly Profile _profile;
    private readonly string? _password;
    private readonly CancellationTokenSource _cts = new();

    public PipelineState State { get; private set; } = PipelineState.Idle;
    public string? LastError { get; private set; }

    public event Action<PipelineState>? StateChanged;
    /// <summary>Se lanza cuando la VPN está lista: la UI debe crear el host RDP y llamar a OnVpnReady.</summary>
    public event Action? VpnReady;

    public Pipeline(Profile profile, string? password)
    {
        _profile = profile;
        _password = password;
    }

    public void Cancel() => _cts.Cancel();

    private void SetState(PipelineState state, string? error = null)
    {
        State = state;
        LastError = error;
        StateChanged?.Invoke(state);
    }

    public async Task RunAsync()
    {
        try
        {
            // 1. Preflight
            if (string.IsNullOrWhiteSpace(_profile.RdpHost))
                throw new InvalidOperationException("El perfil no tiene servidor RDP configurado.");
            if (!File.Exists(_profile.ResolvedTunnelConfPath))
                throw new FileNotFoundException(
                    $"No se encuentra la configuración WireGuard: {_profile.ResolvedTunnelConfPath}");

            Log.Info($"Iniciando conexión: túnel '{_profile.TunnelName}' → RDP {_profile.RdpHost}:{_profile.RdpPort}");

            // 2. Asegurar cliente WireGuard (descarga oficial si no hay MSI local)
            SetState(PipelineState.EnsuringWireGuard);
            if (!WireGuardInstaller.IsWgCliInstalled())
            {
                if (WireGuardInstaller.FindAnyInstaller() is null)
                    await WireGuardInstaller.DownloadLatestInstallerAsync(progress: null, _cts.Token)
                        .ConfigureAwait(false);

                var code = await ElevatedRunner.RunElevatedAsync("install-wireguard")
                    .ConfigureAwait(false);
                if (code != 0 || !WireGuardInstaller.IsWgCliInstalled())
                    throw new InvalidOperationException(
                        "No se pudo instalar el cliente WireGuard (código " + code + ").");
            }
            await WireGuardUi.StopUserInterfaceAsync(_cts.Token).ConfigureAwait(false);
            _cts.Token.ThrowIfCancellationRequested();

            // 3. Importar túnel (elevado si hace falta)
            SetState(PipelineState.ImportingTunnel);
            if (!await TunnelManager.TunnelServiceExistsAsync(_profile.TunnelName, _cts.Token)
                    .ConfigureAwait(false))
            {
                Log.Info($"Importando túnel '{_profile.TunnelName}' (se pedirá UAC)…");
                var code = await ElevatedRunner.RunElevatedAsync(
                    "import-tunnel", _profile.TunnelName, _profile.ResolvedTunnelConfPath)
                    .ConfigureAwait(false);
                if (code != 0)
                    throw new InvalidOperationException(
                        $"No se pudo importar el túnel '{_profile.TunnelName}' (código {code}).");
            }
            else
            {
                Log.Info($"El servicio del túnel '{_profile.TunnelName}' ya existe.");
            }
            await WireGuardUi.StopUserInterfaceAsync(_cts.Token).ConfigureAwait(false);
            _cts.Token.ThrowIfCancellationRequested();

            // 4. Conectar VPN. /installtunnelservice suele dejarlo ya arrancando:
            // EnsureTunnelRunningAsync espera RUNNING sin pelearse con net start.
            SetState(PipelineState.ConnectingVpn);
            await TunnelManager.EnsureTunnelRunningAsync(
                    _profile.TunnelName, TimeSpan.FromSeconds(45), _cts.Token)
                .ConfigureAwait(false);
            await WireGuardUi.StopUserInterfaceAsync(_cts.Token).ConfigureAwait(false);

            // 5. Esperar enlace usable (wg show necesita admin; hay fallbacks ping/TCP)
            SetState(PipelineState.WaitingHandshake);
            if (!await TunnelHealthCheck.WaitForHandshakeAsync(
                    _profile.TunnelName,
                    _profile.ServerVpnIp,
                    _profile.RdpHost,
                    _profile.RdpPort,
                    TimeSpan.FromSeconds(30),
                    _cts.Token).ConfigureAwait(false))
                throw new InvalidOperationException(
                    "El túnel subió pero no hay enlace usable (revise claves/endpoint/firewall " +
                    "o indique la IP VPN del servidor para el ping de salud).");
            await TunnelHealthCheck.PingServerVpnIpAsync(_profile.ServerVpnIp, _cts.Token)
                .ConfigureAwait(false);

            // 6. RDP
            SetState(PipelineState.ConnectingRdp);
            Log.Info("VPN lista; iniciando sesión RDP.");
            VpnReady?.Invoke();
        }
        catch (OperationCanceledException)
        {
            Log.Warn("Proceso cancelado por el usuario.");
            SetState(PipelineState.Finished, "cancelado");
            await TeardownAsync().ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            Log.Error("Pipeline falló", ex);
            SetState(PipelineState.Failed, ex.Message);
            await TeardownAsync().ConfigureAwait(false);
        }
    }

    /// <summary>La UI llama aquí tras conectar el control RDP.</summary>
    public void OnRdpConnecting() => SetState(PipelineState.SessionActive);

    public void OnRdpEnded(bool failed)
    {
        if (failed)
            SetState(PipelineState.Failed, "La sesión RDP terminó con error.");
        else
            SetState(PipelineState.TearingDown);
        _ = TeardownAsync();
    }

    private async Task TeardownAsync()
    {
        try
        {
            var state = await TunnelManager.QueryServiceStateAsync(_profile.TunnelName)
                .ConfigureAwait(false);
            if (state == "RUNNING")
                await TunnelManager.StopTunnelAsync(_profile.TunnelName).ConfigureAwait(false);

            if (_profile.RemoveTunnelOnClose)
                await ElevatedRunner.RunElevatedAsync("remove-tunnel", _profile.TunnelName)
                    .ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            Log.Error("Fallo en el desmontaje del túnel", ex);
        }
        finally
        {
            if (State != PipelineState.Failed)
                SetState(PipelineState.Finished);
        }
    }
}
