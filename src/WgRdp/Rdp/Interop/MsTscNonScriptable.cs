using System.Runtime.InteropServices;

namespace WgRdp.Rdp.Interop;

/// <summary>
/// Interop COM mínimo de MSTSCLib (mstscax.dll) sin necesidad de AxImp.
/// Solo la interfaz no-estándar necesaria para inyectar credenciales en un solo paso.
/// El resto de propiedades se manejan por late-binding (IDispatch/dynamic) sobre el control.
/// IID documentado de IMsTscNonScriptable: {63E3BA7C-6FE6-4624-8659-749BE22B6434}
/// </summary>
[ComImport]
[Guid("63E3BA7C-6FE6-4624-8659-749BE22B6434")]
[InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
public interface IMsTscNonScriptable
{
    // Orden exacto del vtable (slotos intermedios declarados para no desalinear).
    [return: MarshalAs(UnmanagedType.BStr)]
    string get_ClearTextPassword();

    void put_ClearTextPassword([MarshalAs(UnmanagedType.BStr)] string password);

    [return: MarshalAs(UnmanagedType.Struct)]
    object get_PublicKey();

    [return: MarshalAs(UnmanagedType.BStr)]
    string get_FullAddressText();

    [return: MarshalAs(UnmanagedType.BStr)]
    string get_ConnectText();

    [return: MarshalAs(UnmanagedType.BStr)]
    string get_NeedPasswordText();

    int get_ServerConnected();

    /// <summary>Usuario, dominio y contraseña para logon de un solo paso (NLA).</summary>
    void SetClearTextCredentials(
        [MarshalAs(UnmanagedType.BStr)] string domainName,
        [MarshalAs(UnmanagedType.BStr)] string userName,
        [MarshalAs(UnmanagedType.BStr)] string password);
}
