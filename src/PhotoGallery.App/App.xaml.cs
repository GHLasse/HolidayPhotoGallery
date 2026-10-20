using Microsoft.UI.Xaml;
using PhotoGallery.App.Native;
using PhotoGallery.Core.Library;
using PhotoGallery.Core.Localization;

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
        // WinUI can turn an error into an immediate crash without raising either event above, so every
        // exception during startup is recorded; the log shows what was thrown just before a crash.
        AppDomain.CurrentDomain.FirstChanceException += LogStartupException;
        _startup.Start();
    }

    private static readonly System.Diagnostics.Stopwatch _startup = new();
    private static int _startupExceptionCount;

    private static void LogStartupException(object? sender, System.Runtime.ExceptionServices.FirstChanceExceptionEventArgs e)
    {
        if (_startup.Elapsed > TimeSpan.FromSeconds(30) || Interlocked.Increment(ref _startupExceptionCount) > 50)
        {
            AppDomain.CurrentDomain.FirstChanceException -= LogStartupException;
            return;
        }
        try
        {
            Directory.CreateDirectory(LibrarySettings.DefaultDataFolder);
            File.AppendAllText(Path.Combine(LibrarySettings.DefaultDataFolder, "startup.log"),
                $"{DateTime.Now:HH:mm:ss.fff} {e.Exception.GetType().FullName}: {e.Exception.Message}{Environment.NewLine}{Environment.StackTrace}{Environment.NewLine}{Environment.NewLine}");
        }
        catch (IOException)
        {
        }
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

    /// <summary>Where the app looks for language files: the ones it ships with, and the user's own.</summary>
    public static string[] LanguageFolders =>
        [Path.Combine(AppContext.BaseDirectory, "Languages"), UserLanguagesFolder];

    /// <summary>The folder where a new language file can be added (Settings › Language opens it).</summary>
    public static string UserLanguagesFolder => Path.Combine(LibrarySettings.DefaultDataFolder, "Languages");

    protected override void OnLaunched(LaunchActivatedEventArgs args)
    {
        try
        {
            // The language is chosen before the window is built, since its texts are read as it is built.
            var settings = LibrarySettings.Load(Path.Combine(LibrarySettings.DefaultDataFolder, "settings.json"));
            Strings.Use(settings.UiLanguage, LanguageFolders);
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
