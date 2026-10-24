using WgRdp.Linux;
using WgRdp.Logging;
using WgRdp.Profiles;

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
/// Instalar herramientas → subir túnel con wg-quick → comprobar enlace → FreeRDP → bajar túnel.
/// </summary>
public sealed class Pipeline
{
    private readonly Profile _profile;
    private readonly CancellationTokenSource _cts = new();
    private int _teardownOnce;

    public PipelineState State { get; private set; } = PipelineState.Idle;
    public string? LastError { get; private set; }

    public event Action<PipelineState>? StateChanged;
    public event Action? VpnReady;

    public Pipeline(Profile profile, string? password)
    {
        _profile = profile;
        _ = password;
    }

    public void Cancel() => _cts.Cancel();

    public async Task RunAsync()
    {
        try
        {
            if (string.IsNullOrWhiteSpace(_profile.RdpHost))
                throw new InvalidOperationException("El perfil no tiene servidor RDP configurado.");
            if (!File.Exists(_profile.ResolvedTunnelConfPath))
                throw new FileNotFoundException(
                    "No se encuentra la configuración WireGuard: " + _profile.ResolvedTunnelConfPath);

            TunnelManager.ValidateName(_profile.TunnelName);
            Log.Info($"Iniciando conexión: túnel '{_profile.TunnelName}' → RDP {_profile.RdpHost}:{_profile.RdpPort}");

            SetState(PipelineState.EnsuringWireGuard);
            await EnsureToolsAsync().ConfigureAwait(false);
            _cts.Token.ThrowIfCancellationRequested();

            SetState(PipelineState.ConnectingVpn);
            await TunnelManager.UpAsync(_profile.TunnelName, _profile.ResolvedTunnelConfPath, _cts.Token)
                .ConfigureAwait(false);

            SetState(PipelineState.WaitingHandshake);
            if (!await TunnelHealthCheck.WaitForLinkAsync(
                    _profile.TunnelName,
                    _profile.ServerVpnIp,
                    _profile.RdpHost,
                    _profile.RdpPort,
                    TimeSpan.FromSeconds(30),
                    _cts.Token).ConfigureAwait(false))
            {
                throw new InvalidOperationException(
                    "El túnel subió pero no hay enlace usable (revise claves, endpoint y firewall, " +
                    "o indique la IP VPN del servidor).");
            }

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

    public void OnRdpConnecting() => SetState(PipelineState.SessionActive);

    public void OnRdpEnded(bool failed)
    {
        if (failed)
            SetState(PipelineState.Failed, "La sesión RDP terminó con error.");
        else
            SetState(PipelineState.TearingDown);
        _ = TeardownAsync();
    }

    private async Task EnsureToolsAsync()
    {
        var status = LinuxTools.Detect();
        if (status.WireGuard && status.FreeRdp)
        {
            if (!status.SecretTool)
                Log.Warn("secret-tool no está instalado. La contraseña se guarda en un archivo privado.");
            return;
        }

        Log.Info(status.Describe());
        var result = await LinuxPackages.InstallMissingAsync(status, _cts.Token).ConfigureAwait(false);
        if (result.ExitCode is RootRunner.Dismissed or RootRunner.Denied)
            throw new InvalidOperationException("Se canceló o denegó la autorización para instalar los componentes.");
        if (!result.Success)
            throw new InvalidOperationException("No se pudieron instalar WireGuard o FreeRDP.");

        var after = LinuxTools.Detect();
        if (!after.WireGuard || !after.FreeRdp)
            throw new InvalidOperationException("Tras la instalación sigue faltando algún componente. " + after.Describe());
    }

    private async Task TeardownAsync()
    {
        if (Interlocked.Exchange(ref _teardownOnce, 1) == 1)
            return;

        try
        {
            await TunnelManager.DownAsync(_profile.TunnelName, _profile.RemoveTunnelOnClose, CancellationToken.None)
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

    private void SetState(PipelineState state, string? error = null)
    {
        State = state;
        LastError = error;
        StateChanged?.Invoke(state);
    }
}
