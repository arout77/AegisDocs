using System.Text.Json;
using System.Windows;
using System.Windows.Media;

namespace ReceiptCapture;

/// <summary>
/// Settings the sidebar styles (Themes/Shared.xaml) react to. Set once on the sidebar, they flow down to every item in it.
/// </summary>
public static class Nav
{
    /// <summary>True when the sidebar is the narrow icon-only rail. Inherited, so setting it on the sidebar reaches every item.</summary>
    public static readonly DependencyProperty IsCollapsedProperty = DependencyProperty.RegisterAttached(
        "IsCollapsed", typeof(bool), typeof(Nav),
        new FrameworkPropertyMetadata(false, FrameworkPropertyMetadataOptions.Inherits));

    public static bool GetIsCollapsed(DependencyObject d) => (bool)d.GetValue(IsCollapsedProperty);
    public static void SetIsCollapsed(DependencyObject d, bool value) => d.SetValue(IsCollapsedProperty, value);

    /// <summary>Shows a small status dot on a sidebar item (an update is waiting, a folder is being watched).</summary>
    public static readonly DependencyProperty BadgeProperty = DependencyProperty.RegisterAttached(
        "Badge", typeof(bool), typeof(Nav), new FrameworkPropertyMetadata(false));

    public static bool GetBadge(DependencyObject d) => (bool)d.GetValue(BadgeProperty);
    public static void SetBadge(DependencyObject d, bool value) => d.SetValue(BadgeProperty, value);

    /// <summary>The colour of that dot.</summary>
    public static readonly DependencyProperty BadgeBrushProperty = DependencyProperty.RegisterAttached(
        "BadgeBrush", typeof(Brush), typeof(Nav), new FrameworkPropertyMetadata(Brushes.Orange));

    public static Brush GetBadgeBrush(DependencyObject d) => (Brush)d.GetValue(BadgeBrushProperty);
    public static void SetBadgeBrush(DependencyObject d, Brush value) => d.SetValue(BadgeBrushProperty, value);
}

/// <summary>Remembers whether the person collapsed the sidebar, in %LocalAppData%\ReceiptCapture\sidebar.json.</summary>
internal static class SidebarSettings
{
    private sealed class Data
    {
        public bool Collapsed { get; set; }
    }

    private static string FilePath => Path.Combine(AppPaths.DataDir, "sidebar.json");

    public static bool LoadCollapsed()
    {
        try
        {
            return File.Exists(FilePath) && (JsonSerializer.Deserialize<Data>(File.ReadAllText(FilePath))?.Collapsed ?? false);
        }
        catch (Exception)
        {
            return false;
        }
    }

    public static void SaveCollapsed(bool collapsed)
    {
        try
        {
            Directory.CreateDirectory(AppPaths.DataDir);
            File.WriteAllText(FilePath, JsonSerializer.Serialize(new Data { Collapsed = collapsed }));
        }
        catch (Exception)
        {
            // Not being able to remember this isn't worth interrupting the user.
        }
    }
}
