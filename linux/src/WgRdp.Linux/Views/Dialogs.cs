using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Platform.Storage;
using WgRdp.Logging;
using WgRdp.Profiles;

namespace WgRdp.Views;

public sealed record PickedWireGuardConf(string Content, string Label, string? SourcePath);

public static class Dialogs
{
    public static async Task ShowMessageAsync(Window owner, string title, string message)
    {
        var window = new Window
        {
            Title = title,
            Width = 480,
            SizeToContent = SizeToContent.Height,
            MinHeight = 150,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            CanResize = false,
            Background = Brush("#10151C"),
        };
        var button = new Button
        {
            Content = "Aceptar",
            HorizontalAlignment = HorizontalAlignment.Right,
            MinWidth = 100,
        };
        button.Classes.Add("primary");
        button.Click += (_, _) => window.Close();
        window.Content = new StackPanel
        {
            Margin = new Thickness(22),
            Spacing = 16,
            Children =
            {
                new TextBlock { Text = message, TextWrapping = Avalonia.Media.TextWrapping.Wrap },
                button,
            },
        };
        await window.ShowDialog(owner);
    }

    public static async Task<bool> ConfirmAsync(Window owner, string title, string message)
    {
        var window = new Window
        {
            Title = title,
            Width = 460,
            SizeToContent = SizeToContent.Height,
            MinHeight = 160,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            CanResize = false,
            Background = Brush("#10151C"),
        };
        var accepted = false;
        var yes = new Button
        {
            Content = "Sí",
            MinWidth = 100,
            HorizontalAlignment = HorizontalAlignment.Right,
        };
        yes.Classes.Add("primary");
        var no = new Button
        {
            Content = "No",
            MinWidth = 100,
        };
        yes.Click += (_, _) =>
        {
            accepted = true;
            window.Close();
        };
        no.Click += (_, _) => window.Close();
        window.Content = new StackPanel
        {
            Margin = new Thickness(22),
            Spacing = 16,
            Children =
            {
                new TextBlock { Text = message, TextWrapping = Avalonia.Media.TextWrapping.Wrap },
                new StackPanel
                {
                    Orientation = Orientation.Horizontal,
                    HorizontalAlignment = HorizontalAlignment.Right,
                    Spacing = 8,
                    Children = { no, yes },
                },
            },
        };
        await window.ShowDialog(owner);
        return accepted;
    }

    public static async Task<PickedWireGuardConf?> PickWireGuardConfAsync(Window owner)
    {
        try
        {
            if (!owner.StorageProvider.CanOpen)
            {
                await ShowMessageAsync(owner, "Selector no disponible",
                    "Este entorno no admite el selector de archivos. Use la opción de pegar la configuración.");
                return null;
            }

            var files = await owner.StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
            {
                Title = "Seleccione la configuración del túnel WireGuard",
                AllowMultiple = false,
                FileTypeFilter =
                [
                    new FilePickerFileType("Configuración WireGuard (*.conf)")
                    {
                        Patterns = ["*.conf", "*.CONF"],
                    },
                    FilePickerFileTypes.All,
                ],
            });

            if (files.Count == 0)
                return null;

            var picked = files[0];
            var local = picked.TryGetLocalPath();
            string text;
            try
            {
                await using var stream = await picked.OpenReadAsync();
                using var reader = new StreamReader(stream);
                text = await reader.ReadToEndAsync();
            }
            catch when (!string.IsNullOrEmpty(local) && File.Exists(local))
            {
                text = await File.ReadAllTextAsync(local);
            }

            if (!ProfileStore.LooksLikeWireGuard(text))
            {
                await ShowMessageAsync(owner, "Configuración inválida",
                    "El archivo seleccionado no parece una configuración WireGuard (falta [Interface] o [Peer]).");
                return null;
            }

            var label = !string.IsNullOrWhiteSpace(picked.Name)
                ? picked.Name
                : (!string.IsNullOrEmpty(local) ? Path.GetFileName(local) : "wireguard.conf");

            return new PickedWireGuardConf(text.Trim(), label, local);
        }
        catch (Exception ex)
        {
            Log.Error("No se pudo leer el archivo .conf", ex);
            await ShowMessageAsync(owner, "Error al abrir el archivo", ex.Message);
            return null;
        }
    }

    public static async Task<string?> PromptPasteWireGuardConfAsync(Window owner, string? initialText = null)
    {
        var window = new Window
        {
            Title = "Pegar configuración WireGuard",
            Width = 580,
            Height = 460,
            MinWidth = 460,
            MinHeight = 360,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Background = Brush("#10151C"),
        };

        var box = new TextBox
        {
            AcceptsReturn = true,
            TextWrapping = TextWrapping.NoWrap,
            FontFamily = new FontFamily("monospace"),
            VerticalContentAlignment = VerticalAlignment.Top,
            Text = initialText ?? "",
        };

        string? result = null;
        var cancel = new Button { Content = "Cancelar", MinWidth = 100 };
        var save = new Button { Content = "Guardar", MinWidth = 100 };
        save.Classes.Add("primary");

        cancel.Click += (_, _) => window.Close();
        save.Click += async (_, _) =>
        {
            var text = box.Text?.Trim() ?? "";
            if (!ProfileStore.LooksLikeWireGuard(text))
            {
                await ShowMessageAsync(window, "Configuración inválida",
                    "El texto debe incluir las secciones [Interface] y [Peer].");
                return;
            }

            result = text;
            window.Close();
        };

        var layout = new Grid
        {
            RowDefinitions = new RowDefinitions("Auto,*,Auto"),
            Margin = new Thickness(20),
            RowSpacing = 12,
        };
        var hint = new TextBlock
        {
            Text = "Pegue el contenido completo del archivo .conf de WireGuard:",
            Foreground = Brush("#8B9BB0"),
            TextWrapping = TextWrapping.Wrap,
        };
        Grid.SetRow(hint, 0);
        Grid.SetRow(box, 1);

        var buttons = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right,
            Spacing = 8,
            Children = { cancel, save },
        };
        Grid.SetRow(buttons, 2);

        layout.Children.Add(hint);
        layout.Children.Add(box);
        layout.Children.Add(buttons);
        window.Content = layout;

        await window.ShowDialog(owner);
        return result;
    }

    private static IBrush Brush(string hex) => Avalonia.Media.Brush.Parse(hex);
}
