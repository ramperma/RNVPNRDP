using System.Runtime.InteropServices;
using System.Windows.Forms;
using WgRdp.Logging;

namespace WgRdp.Rdp;

/// <summary>
/// Envoltorio AxHost del cliente RDP (mstscax.dll). El CLSID se resuelve en tiempo
/// de ejecución: Windows 10/11 ya no registran MsRdpClient10NotSafeForScripting.
/// </summary>
public sealed class AxMsRdpClient : AxHost
{
    private static readonly string[] CandidateProgIds =
    [
        "MsTscAx.MsTscAx.13",
        "MsTscAx.MsTscAx.12",
        "MsTscAx.MsTscAx.11",
        "MsTscAx.MsTscAx.10",
        "MsTscAx.MsTscAx.9",
        "MsTscAx.MsTscAx.8",
        "MsRDP.MsRDP.12",
        "MsRDP.MsRDP.11",
        "MsRDP.MsRDP.10",
        "MsTscAx.MsTscAx.7",
        "MsTscAx.MsTscAx.6",
        "MsTscAx.MsTscAx.5",
        "MsTscAx.MsTscAx.4",
        "MsTscAx.MsTscAx.3",
        "MsTscAx.MsTscAx.2",
        "MsTscAx.MsTscAx.1",
    ];

    public AxMsRdpClient() : base(ResolveClsid())
    {
    }

    public static string ResolveClsid()
    {
        foreach (var progId in CandidateProgIds)
        {
            Type? type;
            try
            {
                type = Type.GetTypeFromProgID(progId, throwOnError: false);
            }
            catch
            {
                continue;
            }

            if (type is null || type.GUID == Guid.Empty)
                continue;

            object? instance = null;
            try
            {
                instance = Activator.CreateInstance(type);
                if (instance is null)
                    continue;

                var clsid = type.GUID.ToString("B");
                Log.Info($"Control RDP registrado: {progId} {clsid}");
                return clsid;
            }
            catch
            {
                // ProgID en el registro pero CoCreate falla: probar el siguiente.
            }
            finally
            {
                if (instance is not null)
                {
                    try { Marshal.FinalReleaseComObject(instance); } catch { /* ignore */ }
                }
            }
        }

        throw new InvalidOperationException(
            "No hay un control ActiveX de Escritorio remoto registrado en este Windows.");
    }

    /// <summary>Objeto OCX subyacente (RCW con IDispatch dual → usable vía dynamic).</summary>
    public object Ocx
    {
        get
        {
            var ocx = GetOcx();
            if (ocx is null)
                throw new InvalidOperationException(
                    "El control RDP aún no está creado (el handle debe existir antes de usarlo).");
            return ocx;
        }
    }
}
