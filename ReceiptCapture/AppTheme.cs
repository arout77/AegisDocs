using System.Runtime.InteropServices;
using System.Text.Json;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using Microsoft.Win32;

namespace ReceiptCapture;

public enum ThemeChoice { System, Light, Dark }

/// <summary>
/// Light / dark switching. "System" follows the Windows app-mode setting and updates live.
/// The choice is remembered in %LocalAppData%\ReceiptCapture\settings.json.
/// </summary>
public static class AppTheme
{
    private static readonly Color LightAccent = Color.FromRgb(0x4F, 0x46, 0xE5);
    private static readonly Color DarkAccent = Color.FromRgb(0x63, 0x66, 0xF1);

    private static ThemeChoice _choice = ThemeChoice.Light;

    public static ThemeChoice Choice => _choice;
    public static bool IsDark { get; private set; }

    /// <summary>Raised after the theme (and so every colour in the app) has changed.</summary>
    public static event Action? Changed;

    public static void Initialize()
    {
        _choice = LoadChoice();

        SystemEvents.UserPreferenceChanged += (_, e) =>
        {
            if (_choice == ThemeChoice.System &&
                e.Category is UserPreferenceCategory.General or UserPreferenceCategory.Color)
            {
                Application.Current?.Dispatcher.BeginInvoke(new Action(Apply));
            }
        };

        Apply();
    }

    public static void SetChoice(ThemeChoice choice)
    {
        _choice = choice;
        SaveChoice();
        Apply();
    }

    /// <summary>System -> Light -> Dark -> System ...</summary>
    public static void Cycle() => SetChoice(_choice switch
    {
        ThemeChoice.System => ThemeChoice.Light,
        ThemeChoice.Light => ThemeChoice.Dark,
        _ => ThemeChoice.System,
    });

    private static void Apply()
    {
        IsDark = _choice == ThemeChoice.Dark || (_choice == ThemeChoice.System && SystemPrefersDark());

        // 1) ModernWpf: restyles the standard controls.
        ModernWpf.ThemeManager.Current.ApplicationTheme = IsDark ? ModernWpf.ApplicationTheme.Dark : ModernWpf.ApplicationTheme.Light;
        ModernWpf.ThemeManager.Current.AccentColor = IsDark ? DarkAccent : LightAccent;

        // 2) Our own palette: swap Light.xaml <-> Dark.xaml (everything binds with DynamicResource).
        SwapPalette(IsDark ? "Dark" : "Light");

        Changed?.Invoke();
    }

    private static void SwapPalette(string name)
    {
        var dictionaries = Application.Current.Resources.MergedDictionaries;
        var replacement = new ResourceDictionary { Source = new Uri($"pack://application:,,,/Themes/{name}.xaml", UriKind.Absolute) };

        int index = -1;
        for (int i = 0; i < dictionaries.Count; i++)
        {
            var source = dictionaries[i].Source?.OriginalString ?? "";
            if (source.EndsWith("Themes/Light.xaml", StringComparison.OrdinalIgnoreCase) ||
                source.EndsWith("Themes/Dark.xaml", StringComparison.OrdinalIgnoreCase))
            {
                index = i;
                break;
            }
        }

        if (index >= 0) dictionaries[index] = replacement;
        else dictionaries.Add(replacement);
    }

    private static bool SystemPrefersDark()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");
            return key?.GetValue("AppsUseLightTheme") is int value && value == 0;
        }
        catch (Exception)
        {
            return false;
        }
    }

    // ------------------------------------------------------------------
    // Dark title bar (Windows 10 build 18985+ / Windows 11)
    // ------------------------------------------------------------------
    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int size);

    public static void ApplyTitleBar(Window window)
    {
        var handle = new WindowInteropHelper(window).Handle;
        if (handle == IntPtr.Zero) return;

        int useDark = IsDark ? 1 : 0;
        try
        {
            // 20 on current Windows; 19 on early Windows 10 builds.
            if (DwmSetWindowAttribute(handle, 20, ref useDark, sizeof(int)) != 0)
                DwmSetWindowAttribute(handle, 19, ref useDark, sizeof(int));
        }
        catch (Exception)
        {
            // Purely cosmetic: ignore if the platform doesn't support it.
        }
    }

    // ------------------------------------------------------------------
    // Settings file
    // ------------------------------------------------------------------
    internal sealed class Settings
    {
        public string Theme { get; set; } = nameof(ThemeChoice.Light);
    }

    private static ThemeChoice LoadChoice()
    {
        try
        {
            if (!File.Exists(AppPaths.SettingsFile)) return ThemeChoice.Light;
            var settings = JsonSerializer.Deserialize<Settings>(File.ReadAllText(AppPaths.SettingsFile));
            return Enum.TryParse<ThemeChoice>(settings?.Theme, out var choice) ? choice : ThemeChoice.Light;
        }
        catch (Exception)
        {
            return ThemeChoice.Light;
        }
    }

    private static void SaveChoice()
    {
        try
        {
            Directory.CreateDirectory(AppPaths.DataDir);
            File.WriteAllText(AppPaths.SettingsFile, JsonSerializer.Serialize(new Settings { Theme = _choice.ToString() }));
        }
        catch (Exception)
        {
            // Not being able to remember the choice isn't worth interrupting the user.
        }
    }
}
