using System.Windows;

namespace ReceiptCapture;

/// <summary>Small helpers so views and view models can show message boxes without repeating themselves.</summary>
public static class Dialogs
{
    private const string Title = "Office Toolkit";

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
}
