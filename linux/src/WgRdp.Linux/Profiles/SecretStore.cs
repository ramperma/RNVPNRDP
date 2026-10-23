using System.Security.Cryptography;
using System.Text;
using WgRdp.Linux;
using WgRdp.Logging;
using WgRdp.Paths;
using WgRdp.Util;

namespace WgRdp.Profiles;

/// <summary>
/// Contraseña en el anillo de claves (secret-tool / libsecret).
/// Si el servicio no está, queda en un archivo de modo 0600 del usuario.
/// </summary>
public static class SecretStore
{
    public static void SavePassword(string profileName, string password)
    {
        if (password.Contains('\n') || password.Contains('\r'))
            throw new InvalidOperationException("La contraseña no puede contener saltos de línea.");

        if (LinuxTools.Which("secret-tool") is { } tool && TrySecretToolStore(tool, profileName, password))
        {
            TryDeleteFile(profileName);
            Log.Info($"Contraseña guardada en el anillo de claves (perfil '{profileName}').");
            return;
        }

        AppPaths.Ensure();
        var path = FilePath(profileName);
        File.WriteAllText(path, password, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
        AppPaths.TryPrivate(path);
        Log.Warn(
            $"secret-tool no guardó la contraseña del perfil '{profileName}'. " +
            "Quedó en un archivo privado de su usuario.");
    }

    public static string? LoadPassword(string profileName)
    {
        if (LinuxTools.Which("secret-tool") is { } tool)
        {
            var fromKeyring = TrySecretToolLookup(tool, profileName);
            if (fromKeyring is not null)
                return fromKeyring;
        }

        var path = FilePath(profileName);
        if (!File.Exists(path))
            return null;
        return File.ReadAllText(path, Encoding.UTF8);
    }

    public static void DeletePassword(string profileName)
    {
        if (LinuxTools.Which("secret-tool") is { } tool)
        {
            _ = ProcessRunner.RunAsync(
                tool,
                ["clear", "service", "wgrdp", "profile", profileName],
                TimeSpan.FromSeconds(10)).GetAwaiter().GetResult();
        }

        TryDeleteFile(profileName);
    }

    private static bool TrySecretToolStore(string tool, string profileName, string password)
    {
        try
        {
            var result = ProcessRunner.RunAsync(
                tool,
                ["store", "--label", "WG-RDP " + profileName, "service", "wgrdp", "profile", profileName],
                TimeSpan.FromSeconds(20),
                stdin: password).GetAwaiter().GetResult();
            return result.Success;
        }
        catch (Exception ex)
        {
            Log.Warn($"secret-tool store falló: {ex.Message}");
            return false;
        }
    }

    private static string? TrySecretToolLookup(string tool, string profileName)
    {
        try
        {
            var result = ProcessRunner.RunAsync(
                tool,
                ["lookup", "service", "wgrdp", "profile", profileName],
                TimeSpan.FromSeconds(15)).GetAwaiter().GetResult();
            if (!result.Success || result.StdOut.Length == 0)
                return null;
            return result.StdOut.TrimEnd('\r', '\n');
        }
        catch (Exception ex)
        {
            Log.Warn($"secret-tool lookup falló: {ex.Message}");
            return null;
        }
    }

    private static string FilePath(string profileName)
    {
        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(profileName))).ToLowerInvariant();
        return Path.Combine(AppPaths.SecretsDir, hash + ".secret");
    }

    private static void TryDeleteFile(string profileName)
    {
        try
        {
            var path = FilePath(profileName);
            if (File.Exists(path))
                File.Delete(path);
        }
        catch
        {
            // El anillo de claves ya tiene la copia vigente.
        }
    }
}
