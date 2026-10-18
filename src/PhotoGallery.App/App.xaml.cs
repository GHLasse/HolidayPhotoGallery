using Microsoft.UI.Xaml;
using PhotoGallery.App.Native;
using PhotoGallery.Core.Library;

namespace PhotoGallery.App;

public partial class App : Application
{
    private Window? _window;

    public App()
    {
        InitializeComponent();
        // Every helper process (ExifTool and its Perl runtime) ends when the app ends, even after a crash.
        ChildProcessGuard.Enable();
        UnhandledException += (_, e) => LogCrash(e.Exception);
        AppDomain.CurrentDomain.UnhandledException += (_, e) => LogCrash(e.ExceptionObject);
    }

    // Keeps a record of crashes, since a WinUI app otherwise just disappears.
    private static void LogCrash(object? exception)
    {
        try
        {
            var log = Path.Combine(LibrarySettings.DefaultDataFolder, "crash.log");
            Directory.CreateDirectory(LibrarySettings.DefaultDataFolder);
            File.AppendAllText(log, $"{DateTime.Now:yyyy-MM-dd HH:mm:ss} {exception}{Environment.NewLine}{Environment.NewLine}");
        }
        catch (IOException)
        {
        }
    }

    protected override void OnLaunched(LaunchActivatedEventArgs args)
    {
        try
        {
            _window = new MainWindow();
            _window.Activate();
        }
        catch (Exception ex)
        {
            // Errors while building the window bypass UnhandledException and end the app with no trace.
            LogCrash(ex);
            throw;
        }
    }
}
