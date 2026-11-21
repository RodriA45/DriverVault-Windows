using System.Windows;

namespace DriverVault;

public partial class App : Application
{
    public static bool IsDarkTheme { get; private set; } = true;
    public static string? CaptureDarkPath { get; private set; }
    public static string? CaptureLightPath { get; private set; }

    protected override void OnStartup(StartupEventArgs e)
    {
        for (int i = 0; i < e.Args.Length; i++)
        {
            if (e.Args[i].Equals("--capture", StringComparison.OrdinalIgnoreCase) && i + 2 < e.Args.Length)
            {
                CaptureDarkPath = e.Args[i + 1];
                CaptureLightPath = e.Args[i + 2];
                break;
            }
        }

        base.OnStartup(e);
    }

    public static void ApplyTheme(bool dark)
    {
        IsDarkTheme = dark;
        var merged = Current.Resources.MergedDictionaries;
        if (merged.Count == 0) return;

        var newTheme = new ResourceDictionary
        {
            Source = new Uri(dark ? "Themes/ThemeDark.xaml" : "Themes/ThemeLight.xaml", UriKind.Relative)
        };

        merged[0] = newTheme;
    }
}
