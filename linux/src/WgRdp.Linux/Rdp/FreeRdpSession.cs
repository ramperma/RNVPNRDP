using System.Diagnostics;
using System.Text;
using WgRdp.Linux;
using WgRdp.Logging;
using WgRdp.Paths;
using WgRdp.Profiles;

namespace WgRdp.Rdp;

/// <summary>
/// Sesión RDP en su propia ventana. Si existe xfreerdp, la contraseña entra por
/// stdin. Si el equipo usa Remmina (libfreerdp), se abre con un perfil temporal
/// y una instancia aparte de la bandeja que el usuario ya tenga abierta.
/// </summary>
public sealed class FreeRdpSession : IDisposable
{
    private Process? _process;
    private int _finishOnce;
    private string? _secret;
    private string? _profileFile;

    public event Action<bool>? Ended;

    public async Task StartAsync(Profile profile, string password)
    {
        if (password.Contains('\n') || password.Contains('\r'))
            throw new InvalidOperationException("La contraseña no puede contener saltos de línea.");

        if (LinuxTools.FindFreeRdp() is null && LinuxTools.FindRemminaRdp() is not null)
        {
            await StartRemminaAsync(profile, password).ConfigureAwait(false);
            return;
        }

        var binary = LinuxTools.FindFreeRdp()
            ?? throw new InvalidOperationException(
                "No hay cliente RDP. Instale Remmina (con el complemento RDP) o xfreerdp.");

        var host = profile.RdpHost.Trim();
        var port = profile.RdpPort is < 1 or > 65535 ? 3389 : profile.RdpPort;
        var user = profile.Username.Trim();
        RequireToken(host, "servidor");
        RequireToken(user, "usuario");

        var help = await HelpAsync(binary).ConfigureAwait(false);
        if (!help.Contains("from-stdin", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException(
                "Este FreeRDP no admite /from-stdin. Instale freerdp3-x11 (o freerdp 2.2 o posterior).");

        var args = BuildArgs(profile, help, host, port, user);
        _secret = password;

        var psi = new ProcessStartInfo
        {
            FileName = binary,
            UseShellExecute = false,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        foreach (var arg in args)
            psi.ArgumentList.Add(arg);

        _process = Process.Start(psi)
            ?? throw new InvalidOperationException("No se pudo iniciar FreeRDP.");

        _process.OutputDataReceived += (_, e) => LogLine(e.Data);
        _process.ErrorDataReceived += (_, e) => LogLine(e.Data);
        _process.BeginOutputReadLine();
        _process.BeginErrorReadLine();
        _process.EnableRaisingEvents = true;
        _process.Exited += (_, _) => Finish(_process.ExitCode != 0);

        await _process.StandardInput.WriteLineAsync(password).ConfigureAwait(false);
        _process.StandardInput.Close();

        Log.Info(
            $"FreeRDP iniciado (pid {_process.Id}) hacia {host}:{port}. " +
            "Recursos locales: impresoras=" + (profile.RedirectPrinters ? "sí" : "no") +
            ", discos=" + (profile.RedirectDrives ? "sí" : "no") +
            ", portapapeles=" + (profile.RedirectClipboard ? "sí" : "no") + ".");

        if (_process.HasExited)
            Finish(_process.ExitCode != 0);
    }

    public void Disconnect()
    {
        try
        {
            if (_process is { HasExited: false })
                _process.Kill(entireProcessTree: true);
        }
        catch
        {
            // El proceso ya terminó.
        }
    }

    public void Dispose()
    {
        Disconnect();
        _process?.Dispose();
        _process = null;
        _secret = null;
        DeleteProfileFile();
    }

    private async Task StartRemminaAsync(Profile profile, string password)
    {
        var remmina = LinuxTools.FindRemminaRdp()
            ?? throw new InvalidOperationException("No está Remmina con el complemento RDP.");
        var host = profile.RdpHost.Trim();
        var port = profile.RdpPort is < 1 or > 65535 ? 3389 : profile.RdpPort;
        var user = profile.Username.Trim();
        RequireToken(host, "servidor");
        RequireToken(user, "usuario");

        _profileFile = WriteRemminaProfile(profile, host, port, user);
        var appId = "org.wgrdp.session" + Guid.NewGuid().ToString("N")[..12];
        var update = await Util.ProcessRunner.RunAsync(
            remmina,
            ["--gapplication-app-id=" + appId, "--update-profile", _profileFile, "--set-option", "password"],
            TimeSpan.FromSeconds(20),
            stdin: password).ConfigureAwait(false);
        if (!update.Success)
            throw new InvalidOperationException("Remmina no guardó la contraseña de la sesión: " + update.Output);

        _secret = password;
        var psi = new ProcessStartInfo
        {
            FileName = remmina,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        psi.ArgumentList.Add("--gapplication-app-id=" + appId);
        psi.ArgumentList.Add("-c");
        psi.ArgumentList.Add(_profileFile);
        psi.ArgumentList.Add("--no-tray-icon");

        _process = Process.Start(psi)
            ?? throw new InvalidOperationException("No se pudo iniciar Remmina.");
        _process.OutputDataReceived += (_, e) => LogLine(e.Data);
        _process.ErrorDataReceived += (_, e) => LogLine(e.Data);
        _process.BeginOutputReadLine();
        _process.BeginErrorReadLine();
        _process.EnableRaisingEvents = true;
        _process.Exited += (_, _) =>
        {
            DeleteProfileFile();
            Finish(_process.ExitCode != 0);
        };

        Log.Info(
            $"Remmina iniciado (pid {_process.Id}) hacia {host}:{port}. " +
            "Usa el FreeRDP que ya trae Remmina. " +
            "Recursos locales: impresoras=" + (profile.RedirectPrinters ? "sí" : "no") +
            ", discos=" + (profile.RedirectDrives ? "sí" : "no") +
            ", portapapeles=" + (profile.RedirectClipboard ? "sí" : "no") + ".");

        if (_process.HasExited)
        {
            DeleteProfileFile();
            Finish(_process.ExitCode != 0);
        }
    }

    private static string WriteRemminaProfile(Profile profile, string host, int port, string user)
    {
        AppPaths.Ensure();
        var path = Path.Combine(AppPaths.RuntimeDir, "session-" + Guid.NewGuid().ToString("N") + ".remmina");
        var security = profile.AuthenticationLevel switch
        {
            0 => "rdp",
            1 => "tls",
            _ => "nla",
        };
        var depth = profile.ColorDepth is 16 or 32 ? profile.ColorDepth : 32;
        var text = new StringBuilder();
        text.AppendLine("[remmina]");
        text.AppendLine("name=WG-RDP");
        text.AppendLine("protocol=RDP");
        text.AppendLine($"server={host}:{port}");
        text.AppendLine($"username={user}");
        if (!string.IsNullOrWhiteSpace(profile.Domain))
        {
            RequireToken(profile.Domain.Trim(), "dominio");
            text.AppendLine("domain=" + profile.Domain.Trim());
        }

        text.AppendLine("cert_ignore=1");
        text.AppendLine("security=" + security);
        text.AppendLine("shareprinter=" + (profile.RedirectPrinters ? "1" : "0"));
        text.AppendLine("disableclipboard=" + (profile.RedirectClipboard ? "0" : "1"));
        text.AppendLine("resolution_mode=2");
        text.AppendLine("colordepth=" + depth);
        if (profile.RedirectDrives)
        {
            var home = Environment.GetEnvironmentVariable("HOME") ?? "";
            if (home.Length > 0 && !home.Contains(',') && !home.Contains(';') && !home.Contains('\n'))
            {
                text.AppendLine("drive=home," + home);
                text.AppendLine("sharefolder=" + home);
            }
        }

        File.WriteAllText(path, text.ToString());
        AppPaths.TryPrivate(path);
        return path;
    }

    private void DeleteProfileFile()
    {
        if (string.IsNullOrEmpty(_profileFile))
            return;
        try { File.Delete(_profileFile); } catch { /* temporal */ }
        _profileFile = null;
    }

    private void Finish(bool failed)
    {
        if (Interlocked.Exchange(ref _finishOnce, 1) == 1)
            return;
        Ended?.Invoke(failed);
    }

    private void LogLine(string? line)
    {
        if (string.IsNullOrWhiteSpace(line))
            return;
        var text = line.Trim();
        if (_secret is { Length: > 0 })
            text = text.Replace(_secret, "********", StringComparison.Ordinal);
        if (text.Contains("password", StringComparison.OrdinalIgnoreCase) && text.Length < 24)
            return;
        Log.Info("freerdp: " + text);
    }

    private static List<string> BuildArgs(Profile profile, string help, string host, int port, string user)
    {
        var args = new List<string>
        {
            "/v:" + host + ":" + port,
            "/u:" + user,
            "/from-stdin",
            "/t:WG-RDP",
        };

        if (!string.IsNullOrWhiteSpace(profile.Domain))
        {
            RequireToken(profile.Domain.Trim(), "dominio");
            args.Add("/d:" + profile.Domain.Trim());
        }

        if (help.Contains("/cert:", StringComparison.OrdinalIgnoreCase))
            args.Add(help.Contains("tofu", StringComparison.OrdinalIgnoreCase) ? "/cert:tofu" : "/cert:ignore");
        else if (help.Contains("cert-ignore", StringComparison.OrdinalIgnoreCase))
            args.Add("/cert-ignore");

        var sec = profile.AuthenticationLevel switch
        {
            0 => "rdp",
            1 => "tls",
            _ => "nla",
        };
        if (help.Contains("/sec:", StringComparison.OrdinalIgnoreCase))
            args.Add("/sec:" + sec);

        args.Add(profile.RedirectClipboard ? "+clipboard" : "-clipboard");

        if (profile.RedirectPrinters && help.Contains("/printer", StringComparison.OrdinalIgnoreCase))
            args.Add("/printer");

        if (profile.RedirectDrives && help.Contains("/drive:", StringComparison.OrdinalIgnoreCase))
        {
            var home = Environment.GetEnvironmentVariable("HOME") ?? "";
            if (home.Length > 0 && !home.Contains(',') && !home.Contains('\n'))
                args.Add("/drive:home," + home);
        }

        if (help.Contains("dynamic-resolution", StringComparison.OrdinalIgnoreCase))
            args.Add("/dynamic-resolution");

        var width = profile.RdpWidth > 0 ? profile.RdpWidth : 1280;
        var height = profile.RdpHeight > 0 ? profile.RdpHeight : 800;
        if (help.Contains("/size:", StringComparison.OrdinalIgnoreCase))
            args.Add($"/size:{width}x{height}");
        else
        {
            args.Add("/w:" + width);
            args.Add("/h:" + height);
        }

        return args;
    }

    private static async Task<string> HelpAsync(string binary)
    {
        var result = await Util.ProcessRunner.RunAsync(binary, ["--help"], TimeSpan.FromSeconds(15))
            .ConfigureAwait(false);
        var text = result.Output;
        if (!text.Contains("from-stdin", StringComparison.OrdinalIgnoreCase))
        {
            var alt = await Util.ProcessRunner.RunAsync(binary, ["/?"], TimeSpan.FromSeconds(15))
                .ConfigureAwait(false);
            text += "\n" + alt.Output;
        }

        return text;
    }

    private static void RequireToken(string value, string label)
    {
        if (value.Length is 0 or > 256
            || value.Any(c => char.IsWhiteSpace(c) || c is '"' or '\'' or ';' or '\\'))
            throw new InvalidOperationException($"El {label} contiene caracteres no admitidos.");
    }
}
