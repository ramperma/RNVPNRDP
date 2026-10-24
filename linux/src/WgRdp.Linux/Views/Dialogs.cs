using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;

namespace WgRdp.Views;

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

    private static Avalonia.Media.IBrush Brush(string hex) => Avalonia.Media.Brush.Parse(hex);
}
