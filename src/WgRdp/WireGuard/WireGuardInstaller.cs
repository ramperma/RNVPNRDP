using System.Net.Http;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;
using WgRdp.Logging;
using WgRdp.Util;

namespace WgRdp.WireGuard;

/// <summary>Detecta, descarga e instala el cliente Windows de WireGuard (MSI silencioso).</summary>
public static partial class WireGuardInstaller
{
    private const string DownloadBaseUrl = "https://download.wireguard.com/windows-client/";
    /// <summary>Última versión conocida si no se puede listar el directorio oficial.</summary>
    private const string FallbackVersion = "1.1.1";

    /// <summary>Carpeta donde se cachea el MSI descargado (la ve también la instancia elevada).</summary>
    public static string CacheDirectory { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "WgRdp", "setup");

    /// <summary>Ruta del cliente instalado, o null si no lo está.</summary>
    public static string? InstalledDirectory =>
        Environment.Is64BitOperatingSystem
            ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "WireGuard")
            : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), "WireGuard");

    /// <summary>Arquitectura del MSI oficial: amd64, arm64 o x86.</summary>
    public static string MsiArchitecture => RuntimeInformation.OSArchitecture switch
    {
        Architecture.Arm64 => "arm64",
        Architecture.X86 => "x86",
        _ => "amd64",
    };

    public static bool IsWgCliInstalled()
    {
        var dir = InstalledDirectory;
        return dir is not null && File.Exists(Path.Combine(dir, "wg.exe"));
    }

    /// <summary>MSI incluido junto al exe (Setup\wireguard), si existe.</summary>
    public static string? FindBundledInstaller() =>
        NewestMsi(Path.Combine(AppContext.BaseDirectory, "Setup", "wireguard"));

    /// <summary>MSI ya descargado en %ProgramData%\WgRdp\setup, si existe.</summary>
    public static string? FindCachedInstaller() => NewestMsi(CacheDirectory);

    /// <summary>Cualquier instalador disponible localmente (incluido o descargado).</summary>
    public static string? FindAnyInstaller() => FindBundledInstaller() ?? FindCachedInstaller();

    private static string? NewestMsi(string dir)
    {
        if (!Directory.Exists(dir))
            return null;

        var arch = MsiArchitecture;
        var candidates = Directory.GetFiles(dir, "*.msi")
            .Select(path => (Path: path, Info: ParseMsiFileName(Path.GetFileName(path))))
            .Where(x => x.Info is not null)
            .Select(x => (x.Path, Info: x.Info!))
            .ToList();

        if (candidates.Count == 0)
            return null;

        // Preferir la arquitectura del equipo; si no hay, cualquier MSI reconocible.
        var preferred = candidates
            .Where(c => string.Equals(c.Info.Arch, arch, StringComparison.OrdinalIgnoreCase)
                        || c.Info.Arch == "legacy")
            .OrderByDescending(c => c.Info.Version)
            .ThenBy(c => c.Info.Arch == "legacy" ? 1 : 0)
            .FirstOrDefault();

        if (preferred.Path is not null)
            return preferred.Path;

        return candidates
            .OrderByDescending(c => c.Info.Version)
            .Select(c => c.Path)
            .FirstOrDefault();
    }

    private sealed record MsiInfo(string Arch, Version Version);

    /// <summary>
    /// Formatos admitidos:
    ///   wireguard-amd64-1.1.1.msi  (actual)
    ///   WireGuard-Setup-0.5.3.msi (legado)
    /// </summary>
    private static MsiInfo? ParseMsiFileName(string fileName)
    {
        var current = CurrentArchRegex().Match(fileName);
        if (current.Success
            && Version.TryParse(current.Groups[2].Value, out var v1))
            return new MsiInfo(current.Groups[1].Value.ToLowerInvariant(), v1);

        var legacy = LegacySetupRegex().Match(fileName);
        if (legacy.Success
            && Version.TryParse(legacy.Groups[1].Value, out var v2))
            return new MsiInfo("legacy", v2);

        return null;
    }

    [GeneratedRegex(@"^wireguard-(amd64|arm64|x86)-(\d+\.\d+\.\d+)\.msi$", RegexOptions.IgnoreCase)]
    private static partial Regex CurrentArchRegex();

    [GeneratedRegex(@"^WireGuard-Setup-(\d+\.\d+\.\d+)\.msi$", RegexOptions.IgnoreCase)]
    private static partial Regex LegacySetupRegex();

    [GeneratedRegex(@"wireguard-(amd64|arm64|x86)-(\d+\.\d+\.\d+)\.msi", RegexOptions.IgnoreCase)]
    private static partial Regex ListingArchRegex();

    /// <summary>
    /// Descarga el MSI oficial más reciente desde download.wireguard.com
    /// (no requiere admin). Devuelve la ruta local del archivo.
    /// </summary>
    public static async Task<string> DownloadLatestInstallerAsync(
        IProgress<(long Done, long Total)>? progress = null, CancellationToken ct = default)
    {
        using var http = new HttpClient { Timeout = TimeSpan.FromMinutes(5) };
        http.DefaultRequestHeaders.UserAgent.ParseAdd("WgRdp/1.0 (+https://www.wireguard.com/install/)");

        var arch = MsiArchitecture;
        var version = await ResolveLatestVersionAsync(http, arch, ct);
        var fileName = $"wireguard-{arch}-{version}.msi";
        var url = DownloadBaseUrl + fileName;
        var target = Path.Combine(CacheDirectory, fileName);

        if (File.Exists(target) && new FileInfo(target).Length > 0)
        {
            Log.Info($"Instalador ya descargado: {target}");
            var len = new FileInfo(target).Length;
            progress?.Report((len, len));
            return target;
        }

        Directory.CreateDirectory(CacheDirectory);
        Log.Info($"Descargando {url} ...");

        using var response = await http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, ct);
        if (!response.IsSuccessStatusCode)
        {
            throw new HttpRequestException(
                $"No se pudo descargar WireGuard ({(int)response.StatusCode} {response.ReasonPhrase}). " +
                $"URL: {url}. Compruebe la conexión o coloque el MSI en Setup\\wireguard\\.");
        }

        var total = response.Content.Headers.ContentLength ?? -1;
        var temp = target + ".part";

        try
        {
            await using (var src = await response.Content.ReadAsStreamAsync(ct))
            await using (var dst = File.Create(temp))
            {
                var buffer = new byte[64 * 1024];
                long done = 0;
                int read;
                while ((read = await src.ReadAsync(buffer, ct)) > 0)
                {
                    await dst.WriteAsync(buffer.AsMemory(0, read), ct);
                    done += read;
                    progress?.Report((done, total));
                }
            }

            if (new FileInfo(temp).Length == 0)
                throw new InvalidOperationException("La descarga de WireGuard quedó vacía.");

            File.Move(temp, target, overwrite: true);
        }
        catch
        {
            try { if (File.Exists(temp)) File.Delete(temp); } catch { /* ignore */ }
            throw;
        }

        Log.Info($"Descarga completada: {target} ({new FileInfo(target).Length / 1024} KB)");
        return target;
    }

    private static async Task<string> ResolveLatestVersionAsync(
        HttpClient http, string arch, CancellationToken ct)
    {
        try
        {
            var listing = await http.GetStringAsync(DownloadBaseUrl, ct);
            var versions = ListingArchRegex().Matches(listing)
                .Where(m => string.Equals(m.Groups[1].Value, arch, StringComparison.OrdinalIgnoreCase))
                .Select(m => Version.Parse(m.Groups[2].Value))
                .Distinct()
                .OrderByDescending(v => v)
                .ToList();

            if (versions.Count > 0)
            {
                var version = versions[0].ToString();
                Log.Info($"Última versión de WireGuard ({arch}) detectada: {version}");
                return version;
            }

            Log.Warn($"El listado de {DownloadBaseUrl} no contiene MSI para {arch}; usando {FallbackVersion}.");
        }
        catch (Exception ex)
        {
            Log.Warn($"No se pudo listar {DownloadBaseUrl} ({ex.Message}); usando {FallbackVersion}.");
        }

        return FallbackVersion;
    }

    /// <summary>Instala el MSI en silencio. Debe ejecutarse elevado.</summary>
    public static async Task<ProcessResult> InstallSilentlyAsync(CancellationToken ct = default)
    {
        var msi = FindAnyInstaller();
        if (msi is null)
            return new ProcessResult(-1, "",
                "No hay instalador local. Descárguelo antes (asistente o Descargar e instalar).");

        Log.Info($"Instalando WireGuard en silencio: {msi}");
        var result = await ProcessRunner.RunAsync(
            "msiexec.exe", $"/i \"{msi}\" /qn /norestart", TimeSpan.FromMinutes(10), ct);

        if (result.Success)
            Log.Info("WireGuard instalado correctamente.");
        else
            Log.Error($"Fallo msiexec (código {result.ExitCode}): {result.Output}");

        return result;
    }
}
