using System.Windows;
using WgRdp.Elevation;
using WgRdp.Logging;

namespace WgRdp;

public partial class App : Application
{
    protected override void OnStartup(StartupEventArgs e)
    {
        Log.Init();

        var elevated = ElevatedRunner.IsElevatedMode(e.Args);

        DispatcherUnhandledException += (_, args) =>
        {
            Log.Error("Excepción no controlada en la interfaz", args.Exception);
            if (!elevated)
            {
                MessageBox.Show(
                    args.Exception.Message,
                    "WgRdp",
                    MessageBoxButton.OK,
                    MessageBoxImage.Error);
            }
            args.Handled = true;
        };

        AppDomain.CurrentDomain.UnhandledException += (_, args) =>
        {
            if (args.ExceptionObject is Exception ex)
                Log.Error("Excepción no controlada", ex);
        };

        // Modo elevado: sin ventana. StartupUri no se usa (WPF no admite asignarlo a null).
        ShutdownMode = ShutdownMode.OnExplicitShutdown;
        if (elevated)
        {
            _ = RunElevatedAndExitAsync(e.Args);
            return;
        }

        base.OnStartup(e);
        new MainWindow().Show();
    }

    private async Task RunElevatedAndExitAsync(string[] args)
    {
        var code = await ElevatedRunner.ExecuteElevatedCommandAsync(args);
        Shutdown(code);
    }
}
