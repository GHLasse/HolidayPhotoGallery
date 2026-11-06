using Microsoft.UI.Xaml;
using PhotoGallery.App.Native;
using PhotoGallery.Core;
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
        // WinUI can turn an error into an immediate crash without raising either event above, so the app
        // keeps a log of what it did and what was thrown; its last lines show what led up to a crash.
        StartActivityLog();
        AppDomain.CurrentDomain.FirstChanceException += LogException;
        _startup.Start();
    }

    private static readonly System.Diagnostics.Stopwatch _startup = new();
    private static readonly Lock _logLock = new();
    private static int _exceptionCount;

    private static string ActivityLog => Path.Combine(LibrarySettings.DefaultDataFolder, "activity.log");

    // One log per run; the run before is kept as activity.old.log, e.g. to look at after a crash.
    private static void StartActivityLog()
    {
        try
        {
            Directory.CreateDirectory(LibrarySettings.DefaultDataFolder);
            if (File.Exists(ActivityLog))
                File.Move(ActivityLog, Path.Combine(LibrarySettings.DefaultDataFolder, "activity.old.log"), overwrite: true);
            File.Delete(Path.Combine(LibrarySettings.DefaultDataFolder, "startup.log")); // its old name
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }
    }

    // During startup with the full call stack; later just what was thrown where, and not without end.
    private static void LogException(object? sender, System.Runtime.ExceptionServices.FirstChanceExceptionEventArgs e)
    {
        var count = Interlocked.Increment(ref _exceptionCount);
        if (count > 1000)
            return;
        var during = _startup.Elapsed < TimeSpan.FromSeconds(30) && count <= 50;
        Write($"{e.Exception.GetType().FullName}: {e.Exception.Message}" +
              (during ? Environment.NewLine + Environment.StackTrace : $" (in {e.Exception.TargetSite?.DeclaringType?.Name}.{e.Exception.TargetSite?.Name})"));
    }

    /// <summary>
    /// Notes a step in activity.log (a window change, a pane sliding, banners), so a crash inside WinUI,
    /// which leaves no exception of ours, shows what the app was doing just before.
    /// </summary>
    internal static void Trace(string step) => Write(step);

    private static void Write(string text)
    {
        try
        {
            lock (_logLock)
                File.AppendAllText(ActivityLog, $"{DateTime.Now:HH:mm:ss.fff} {LogText.Redact(text)}{Environment.NewLine}");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
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
            File.AppendAllText(log, $"{DateTime.Now:yyyy-MM-dd HH:mm:ss} {LogText.Redact(exception?.ToString() ?? "")}{Environment.NewLine}{Environment.NewLine}");
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
            var language = Strings.Use(settings.UiLanguage, LanguageFolders);
            // Month and day names follow the app's language; the date order and numbers stay Windows' own.
            var culture = Strings.WithNamesOf(System.Globalization.CultureInfo.CurrentCulture, language);
            System.Globalization.CultureInfo.CurrentCulture = culture;
            System.Globalization.CultureInfo.DefaultThreadCurrentCulture = culture;
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
