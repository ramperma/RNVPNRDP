using System.Net.Sockets;
using WgRdp.Paths;

namespace WgRdp;

/// <summary>
/// Una sola instancia. Si el programa ya está en marcha, la nueva avisa
/// por un socket de usuario y termina, para no acumular iconos de bandeja.
/// </summary>
public static class InstanceLock
{
    private static readonly object Gate = new();
    private static readonly List<Action> Handlers = [];
    private static FileStream? _lockFile;
    private static Socket? _listen;
    private static string? _path;
    private static bool _pending;
    private static int _released;

    public static bool TryBecomePrimary()
    {
        AppPaths.Ensure();
        var sockPath = Path.Combine(AppPaths.RuntimeDir, "instance.sock");
        var lockPath = Path.Combine(AppPaths.RuntimeDir, "instance.lock");

        try
        {
            _lockFile = new FileStream(lockPath, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
        }
        catch (IOException)
        {
            for (var attempt = 0; attempt < 25; attempt++)
            {
                if (TryNotify(sockPath))
                    return false;
                Thread.Sleep(100);
            }

            return false;
        }

        TryDelete(sockPath);
        var socket = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
        socket.Bind(new UnixDomainSocketEndPoint(sockPath));
        AppPaths.TryPrivate(sockPath);
        socket.Listen(4);
        _path = sockPath;
        _listen = socket;
        _ = Task.Run(AcceptLoop);
        return true;
    }

    public static void Listen(Action onActivate)
    {
        bool pending;
        lock (Gate)
        {
            Handlers.Add(onActivate);
            pending = _pending;
            _pending = false;
        }

        if (pending)
            onActivate();
    }

    public static void Release()
    {
        if (Interlocked.Exchange(ref _released, 1) == 1)
            return;

        try { _listen?.Close(); } catch { /* el bucle de escucha termina */ }
        _listen = null;
        if (_path is not null)
            TryDelete(_path);
        try { _lockFile?.Dispose(); } catch { /* el siguiente arranque vuelve a tomar el bloqueo */ }
        _lockFile = null;
    }

    private static void AcceptLoop()
    {
        var listen = _listen;
        if (listen is null)
            return;

        while (Volatile.Read(ref _released) == 0)
        {
            Socket client;
            try
            {
                client = listen.Accept();
            }
            catch
            {
                return;
            }

            using (client)
                Raise();
        }
    }

    private static void Raise()
    {
        Action[] handlers;
        lock (Gate)
        {
            if (Handlers.Count == 0)
            {
                _pending = true;
                return;
            }

            handlers = [.. Handlers];
        }

        foreach (var handler in handlers)
            handler();
    }

    private static bool TryNotify(string path)
    {
        if (!File.Exists(path))
            return false;

        try
        {
            using var client = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
            client.Connect(new UnixDomainSocketEndPoint(path));
            client.Send("show\n"u8);
            return true;
        }
        catch (SocketException)
        {
            return false;
        }
    }

    private static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path))
                File.Delete(path);
        }
        catch
        {
            // Si sigue ocupado, el enlace fallará y el arranque lo mostrará.
        }
    }
}
