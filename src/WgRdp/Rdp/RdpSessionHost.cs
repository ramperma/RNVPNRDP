using System.Runtime.InteropServices;
using WgRdp.Logging;
using WgRdp.Profiles;
using WgRdp.Rdp.Interop;

namespace WgRdp.Rdp;

public enum RdpSessionState
{
    Idle,
    Connecting,
    Connected,
    Disconnected,
    Failed,
}

/// <summary>
/// Controla la sesión RDP incrustada: credenciales, conexión y detección de fin
/// por sondeo de la propiedad Connected (sin connection-point sink en la v1).
/// </summary>
public sealed class RdpSessionHost : IDisposable
{
    private readonly AxMsRdpClient _control;
    private System.Windows.Forms.Timer? _watchdog;
    private bool _everConnected;

    public RdpSessionState State { get; private set; } = RdpSessionState.Idle;

    public event Action<RdpSessionState>? StateChanged;

    public AxMsRdpClient Control => _control;

    public RdpSessionHost()
    {
        // No CreateControl aquí: el handle lo crea WindowsFormsHost al asignar Child.
        _control = new AxMsRdpClient();
    }

    public void Connect(Profile profile, string? password)
    {
        if (!_control.IsHandleCreated)
            _control.CreateControl();

        dynamic ocx = _control.Ocx;

        ocx.Server = profile.RdpHost ?? "";
        ocx.UserName = profile.Username ?? "";
        ocx.Domain = profile.Domain ?? "";

        try
        {
            dynamic adv = ocx.AdvancedSettings2;
            adv.RdpPort = profile.RdpPort;
            adv.AuthenticationLevel = profile.AuthenticationLevel;
            adv.ClearTextPassword = password ?? "";
        }
        catch (COMException ex)
        {
            Log.Warn($"AdvancedSettings2 no disponible ({ex.HResult:x8}); usando valores por defecto.");
        }

        try
        {
            dynamic adv7 = ocx.AdvancedSettings7;
            adv7.ConnectToAdministerServer = false;
        }
        catch (COMException)
        {
            // MsRdpClient10 puede no exponer AdvancedSettings7; no es crítico.
        }

        try
        {
            ocx.ColorDepth = profile.ColorDepth;
            if (profile.RdpWidth > 0 && profile.RdpHeight > 0)
            {
                ocx.DesktopWidth = profile.RdpWidth;
                ocx.DesktopHeight = profile.RdpHeight;
            }
        }
        catch (COMException)
        {
            // opcional
        }

        ApplyLocalResources(ocx, profile);

        // Inyección de credenciales de un solo paso (recomendado para NLA).
        if (!TrySetClearTextCredentials(profile, password))
        {
            // Fallback: propiedad ClearTextPassword vía IDispatch.
            try { ocx.ClearTextPassword = password ?? ""; }
            catch (COMException ex) { Log.Warn($"No se pudo fijar ClearTextPassword: {ex.Message}"); }
        }

        SetState(RdpSessionState.Connecting);
        ocx.Connect();
        StartWatchdog();
    }

    /// <summary>Impresoras, discos y portapapeles del equipo local hacia la sesión remota.</summary>
    private static void ApplyLocalResources(dynamic ocx, Profile profile)
    {
        try
        {
            dynamic adv = ocx.AdvancedSettings2;
            adv.RedirectPrinters = profile.RedirectPrinters;
            adv.RedirectDrives = profile.RedirectDrives;
        }
        catch (Exception ex)
        {
            Log.Warn($"No se pudo aplicar impresoras/discos locales: {ex.Message}");
        }

        var clipboardApplied = false;
        try
        {
            dynamic adv5 = ocx.AdvancedSettings5;
            adv5.RedirectClipboard = profile.RedirectClipboard;
            clipboardApplied = true;
        }
        catch (Exception)
        {
            // AdvancedSettings5 no está en todos los ProgID; se intenta en AdvancedSettings2.
        }

        if (!clipboardApplied)
        {
            try
            {
                dynamic adv = ocx.AdvancedSettings2;
                adv.RedirectClipboard = profile.RedirectClipboard;
            }
            catch (Exception ex)
            {
                Log.Warn($"No se pudo aplicar el portapapeles local: {ex.Message}");
            }
        }

        Log.Info(
            "Recursos locales: impresoras=" + YesNo(profile.RedirectPrinters) +
            ", discos=" + YesNo(profile.RedirectDrives) +
            ", portapapeles=" + YesNo(profile.RedirectClipboard) + ".");
    }

    private static string YesNo(bool value) => value ? "sí" : "no";

    /// <summary>QueryInterface manual de IMsTscNonScriptable (no expuesta por IDispatch).</summary>
    private bool TrySetClearTextCredentials(Profile profile, string? password)
    {
        if (string.IsNullOrEmpty(password))
            return false;

        try
        {
            var ocxObj = _control.Ocx;
            var nonScriptable = QueryNonScriptable(ocxObj);
            if (nonScriptable is null)
                return false;

            nonScriptable.SetClearTextCredentials(
                profile.Domain ?? "",
                profile.Username,
                password);

            Log.Info("Credenciales inyectadas vía SetClearTextCredentials.");
            return true;
        }
        catch (Exception ex)
        {
            Log.Warn($"SetClearTextCredentials no disponible: {ex.GetType().Name}: {ex.Message}");
            return false;
        }
    }

    /// <summary>QueryInterface explícito de IMsTscNonScriptable sobre el RCW del control.</summary>
    private static IMsTscNonScriptable? QueryNonScriptable(object ocxObj)
    {
        var pUnk = Marshal.GetIUnknownForObject(ocxObj);
        try
        {
            var iid = typeof(IMsTscNonScriptable).GUID;
            int hr = Marshal.QueryInterface(pUnk, ref iid, out var pIface);
            if (hr != 0 || pIface == IntPtr.Zero)
                return null;
            try
            {
                return (IMsTscNonScriptable)Marshal.GetUniqueObjectForIUnknown(pIface);
            }
            finally
            {
                Marshal.Release(pIface);
            }
        }
        finally
        {
            Marshal.Release(pUnk);
        }
    }

    public void Disconnect()
    {
        try
        {
            dynamic ocx = _control.Ocx;
            if ((bool)ocx.Connected)
                ocx.Disconnect();
        }
        catch (COMException ex)
        {
            Log.Warn($"Disconnect: {ex.Message}");
        }
    }

    private void StartWatchdog()
    {
        _watchdog?.Dispose();
        _watchdog = new System.Windows.Forms.Timer { Interval = 1000 };
        _watchdog.Tick += (_, _) => Poll();
        _watchdog.Start();
    }

    private void Poll()
    {
        bool connected;
        try
        {
            connected = (bool)((dynamic)_control.Ocx).Connected;
        }
        catch (COMException)
        {
            connected = false;
        }

        if (connected)
        {
            if (!_everConnected)
            {
                _everConnected = true;
                Log.Info("Sesión RDP establecida.");
                SetState(RdpSessionState.Connected);
            }
            return;
        }

        if (_everConnected && State == RdpSessionState.Connected)
        {
            Log.Info("Sesión RDP finalizada.");
            SetState(RdpSessionState.Disconnected);
        }
        else if (State == RdpSessionState.Connecting)
        {
            // Sin conexión tras un tiempo: el control ya habrá mostrado su propio error.
            if (!_everConnected && DateTime.UtcNow - _connectStarted > TimeSpan.FromSeconds(30))
            {
                Log.Warn("La conexión RDP no llegó a establecerse.");
                SetState(RdpSessionState.Failed);
            }
        }
    }

    private DateTime _connectStarted = DateTime.UtcNow;

    private void SetState(RdpSessionState state)
    {
        if (state == RdpSessionState.Connecting)
            _connectStarted = DateTime.UtcNow;
        State = state;
        StateChanged?.Invoke(state);
    }

    public void Dispose()
    {
        _watchdog?.Dispose();
        _watchdog = null;
        try { Disconnect(); } catch { /* cierre */ }
        _control.Dispose();
    }
}
