using Microsoft.UI.Xaml;

namespace Net.ViaTheFalcon.JnapUpMon.Remote;

/// <summary>
/// Application entry point. Creates and activates the single main window.
/// </summary>
public partial class App : Application
{
    private Window? _window;

    public App()
    {
        InitializeComponent();

        // A hardware-facing app should never be brought down by a transient
        // Bluetooth/WinRT error escaping to the dispatcher; log and carry on.
        UnhandledException += (_, e) =>
        {
            Log(e.Exception);
            e.Handled = true;
        };
    }

    protected override void OnLaunched(LaunchActivatedEventArgs args)
    {
        _window = new MainWindow();
        _window.Activate();
    }

    private static void Log(System.Exception ex)
    {
        try
        {
            string path = System.IO.Path.Combine(
                System.AppContext.BaseDirectory, "error.log");
            System.IO.File.AppendAllText(
                path, $"[{System.DateTimeOffset.Now:o}] {ex}\n\n");
        }
        catch
        {
            // Logging is best-effort.
        }
    }
}
