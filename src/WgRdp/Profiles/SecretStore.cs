using System.Runtime.InteropServices;
using System.Text;
using WgRdp.Logging;

namespace WgRdp.Profiles;

/// <summary>
/// Secreto en el Administrador de credenciales de Windows (nunca en disco en claro).
/// Target: "WgRdp/&lt;nombre-perfil&gt;".
/// </summary>
public static class SecretStore
{
    private const uint CRED_TYPE_GENERIC = 1;
    private const uint CRED_PERSIST_LOCAL_MACHINE = 2;
    private const int CRED_MAX_CREDENTIAL_BLOB_SIZE = 512 * 512;

    public static string TargetFor(string profileName) => "WgRdp/" + profileName;

    public static void SavePassword(string profileName, string password)
    {
        var target = TargetFor(profileName);
        var blob = Encoding.UTF8.GetBytes(password);

        var cred = new CREDENTIAL
        {
            Type = CRED_TYPE_GENERIC,
            TargetName = target,
            Persist = CRED_PERSIST_LOCAL_MACHINE,
            AttributeCount = 0,
            CredentialBlobSize = (uint)blob.Length,
        };

        var blobPtr = Marshal.AllocHGlobal(blob.Length);
        try
        {
            Marshal.Copy(blob, 0, blobPtr, blob.Length);
            cred.CredentialBlob = blobPtr;

            if (!CredWriteW(ref cred, 0))
            {
                var err = Marshal.GetLastWin32Error();
                throw new InvalidOperationException(
                    $"CredWrite falló para '{target}' (GetLastWin32Error={err}). " +
                    "Compruebe que el almacén de credenciales no esté lleno (máx. 512 bytes por secreto).");
            }

            Log.Info($"Contraseña guardada en el almacén de credenciales ({target}).");
        }
        finally
        {
            Marshal.FreeHGlobal(blobPtr);
        }
    }

    public static string? LoadPassword(string profileName)
    {
        var target = TargetFor(profileName);
        if (!CredReadW(target, CRED_TYPE_GENERIC, 0, out var credPtr))
            return null;

        try
        {
            var cred = Marshal.PtrToStructure<CREDENTIAL>(credPtr);
            if (cred.CredentialBlob == IntPtr.Zero || cred.CredentialBlobSize == 0)
                return null;
            var blob = new byte[cred.CredentialBlobSize];
            Marshal.Copy(cred.CredentialBlob, blob, 0, blob.Length);
            return Encoding.UTF8.GetString(blob);
        }
        finally
        {
            CredFree(credPtr);
        }
    }

    public static void DeletePassword(string profileName)
    {
        CredDeleteW(TargetFor(profileName), CRED_TYPE_GENERIC, 0);
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct CREDENTIAL
    {
        public uint Flags;
        public uint Type;
        [MarshalAs(UnmanagedType.LPWStr)] public string TargetName;
        [MarshalAs(UnmanagedType.LPWStr)] public string? Comment;
        public long LastWritten;
        public uint CredentialBlobSize;
        public IntPtr CredentialBlob;
        public uint Persist;
        public uint AttributeCount;
        public IntPtr Attributes;
        [MarshalAs(UnmanagedType.LPWStr)] public string? TargetAliasName;
        [MarshalAs(UnmanagedType.LPWStr)] public string? UserName;
    }

    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool CredWriteW(ref CREDENTIAL userCredential, uint flags);

    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool CredReadW(
        string target, uint type, uint flags, out IntPtr credentialPtr);

    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool CredDeleteW(string target, uint type, uint flags);

    [DllImport("advapi32.dll")]
    private static extern void CredFree(IntPtr cred);
}
