using System.Windows;
using System.Windows.Controls;

namespace ReceiptCapture;

/// <summary>Small helpers so views and view models can show message boxes without repeating themselves.</summary>
public static class Dialogs
{
    private const string Title = "Aegis Docs";

    public static void Info(DependencyObject anchor, string text)
    {
        var owner = Window.GetWindow(anchor);
        if (owner != null) MessageBox.Show(owner, text, Title, MessageBoxButton.OK, MessageBoxImage.Information);
        else MessageBox.Show(text, Title, MessageBoxButton.OK, MessageBoxImage.Information);
    }

    public static bool Confirm(DependencyObject anchor, string text)
    {
        var owner = Window.GetWindow(anchor);
        var result = owner != null
            ? MessageBox.Show(owner, text, Title, MessageBoxButton.YesNo, MessageBoxImage.Question)
            : MessageBox.Show(text, Title, MessageBoxButton.YesNo, MessageBoxImage.Question);
        return result == MessageBoxResult.Yes;
    }

    /// <summary>
    /// Asks for a document's password. Returns what was typed, or null if the person cancelled.
    /// The password is only kept in memory for the job at hand.
    /// </summary>
    public static string? AskPassword(DependencyObject anchor, string fileName, bool previousWasWrong)
    {
        var owner = Window.GetWindow(anchor);

        var prompt = new TextBlock
        {
            TextWrapping = TextWrapping.Wrap,
            MaxWidth = 360,
            Text = previousWasWrong
                ? $"That password didn't work. Try again for {fileName}:"
                : $"{fileName} is password-protected. Enter its password to open it:",
        };
        var box = new PasswordBox { Margin = new Thickness(0, 10, 0, 0), MinWidth = 300 };
        var open = new Button { Content = "Open", IsDefault = true, MinWidth = 84, Margin = new Thickness(0, 0, 8, 0) };
        var cancel = new Button { Content = "Cancel", IsCancel = true, MinWidth = 84 };
        var buttons = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right,
            Margin = new Thickness(0, 16, 0, 0),
            Children = { open, cancel },
        };

        var window = new Window
        {
            Title = Title,
            Content = new StackPanel { Margin = new Thickness(20), Children = { prompt, box, buttons } },
            SizeToContent = SizeToContent.WidthAndHeight,
            ResizeMode = ResizeMode.NoResize,
            ShowInTaskbar = false,
            Owner = owner,
            WindowStartupLocation = owner != null ? WindowStartupLocation.CenterOwner : WindowStartupLocation.CenterScreen,
        };

        string? result = null;
        open.Click += (_, _) =>
        {
            if (box.Password.Length == 0) return;      // nothing typed yet: keep the box open
            result = box.Password;
            window.DialogResult = true;
        };
        window.Loaded += (_, _) => box.Focus();

        return window.ShowDialog() == true ? result : null;
    }
}
