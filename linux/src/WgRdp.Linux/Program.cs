using Avalonia;
using WgRdp.Linux;

namespace WgRdp;

internal static class Program
{
    [STAThread]
    public static void Main(string[] args)
    {
        if (args.Any(arg => arg is "--doctor" or "--comprobar"))
        {
            Console.WriteLine(LinuxTools.Report());
            return;
        }

        if (!InstanceLock.TryBecomePrimary())
            return;

        BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
    }

    public static AppBuilder BuildAvaloniaApp() =>
        AppBuilder.Configure<App>()
            .UsePlatformDetect()
            .WithInterFont()
            .LogToTrace();
}
