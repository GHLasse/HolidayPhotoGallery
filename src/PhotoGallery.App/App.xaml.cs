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
        // The log comes first, so that even a crash while the app is being set up leaves a trace.
        StartActivityLog();
        InitializeComponent();
        // Every helper process (ExifTool and its Perl runtime) ends when the app ends, even after a crash.
        ChildProcessGuard.Enable();
        UnhandledException += OnUnhandledException;
        AppDomain.CurrentDomain.UnhandledException += (_, e) =>
            LogCrash(e.ExceptionObject, e.IsTerminating ? "Unhandled exception, the app ends" : "Unhandled exception");
        // A background task that failed and was never awaited: noted, it doesn't end the app.
        TaskScheduler.UnobservedTaskException += (_, e) =>
        {
            Write($"Background task failed: {e.Exception.GetBaseException()}");
            e.SetObserved();
        };
        // WinUI can turn an error into an immediate crash without raising any event above, so the app
        // keeps a log of what it did and what was thrown; its last lines show what led up to a crash, and
        // the next start copies them into crash.log when the run before didn't end properly.
        AppDomain.CurrentDomain.FirstChanceException += LogException;
        _startup.Start();
    }

    private static readonly System.Diagnostics.Stopwatch _startup = new();
    private static readonly Lock _logLock = new();
    private static int _exceptionCount;
    // Written last when the app ends properly; a log without it belongs to a run that crashed or was killed.
    private const string EndMarker = "app ended";
    private const string StartMarker = "app started";
    private const string CrashWritten = "crash written to crash.log";

    private static string ActivityLog => Path.Combine(LibrarySettings.DefaultDataFolder, "activity.log");
    private static string CrashLog => Path.Combine(LibrarySettings.DefaultDataFolder, "crash.log");

    // One log per run; the run before is kept as activity.old.log, e.g. to look at after a crash.
    private static void StartActivityLog()
    {
        try
        {
            Directory.CreateDirectory(LibrarySettings.DefaultDataFolder);
            if (File.Exists(ActivityLog))
            {
                NoteUnfinishedRun();
                File.Move(ActivityLog, Path.Combine(LibrarySettings.DefaultDataFolder, "activity.old.log"), overwrite: true);
            }
            File.Delete(Path.Combine(LibrarySettings.DefaultDataFolder, "startup.log")); // its old name
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }
        Write($"{StartMarker}: version {VersionText}, Windows {Environment.OSVersion.Version}, process {Environment.ProcessId}");
    }

    private static string VersionText
    {
        get
        {
            try
            {
                return typeof(App).Assembly.GetName().Version is { } v ? $"{v.Major}.{v.Minor}.{Math.Max(0, v.Build)}" : "?";
            }
            catch (Exception)
            {
                return "?";
            }
        }
    }

    /// <summary>
    /// The run before ended without closing its window (a crash inside WinUI ends the app at once, with no event of
    /// ours): its last steps go into crash.log, so the report holds them even after the app has been started again.
    /// </summary>
    private static void NoteUnfinishedRun()
    {
        try
        {
            var lines = File.ReadAllLines(ActivityLog);
            var last = lines.LastOrDefault(l => l.Length > 0) ?? "";
            if (last.EndsWith(EndMarker, StringComparison.Ordinal) || lines.Length == 0)
                return;
            // A log that ends with a crash LogCrash already copied: copying it again adds nothing.
            if (last.EndsWith(CrashWritten, StringComparison.Ordinal))
                return;
            var started = lines[0].Length >= 12 ? lines[0][..12] : "?";
            AppendCrash($"The previous run (started {File.GetLastWriteTime(ActivityLog):yyyy-MM-dd} {started}) ended without closing (a crash inside WinUI or the " +
                        $"process was ended). Its last {Math.Min(lines.Length, TailLines)} activity lines:" + Environment.NewLine +
                        string.Join(Environment.NewLine, lines.TakeLast(TailLines)));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }
    }

    private const int TailLines = 80;

    /// <summary>Notes that the app ends properly (its window closed), so the next start doesn't report a crash.</summary>
    internal static void Ended() => Write(EndMarker);

    // During startup with the full call stack; later just what was thrown where, and not without end.
    private static void LogException(object? sender, System.Runtime.ExceptionServices.FirstChanceExceptionEventArgs e)
    {
        var count = Interlocked.Increment(ref _exceptionCount);
        if (count > 1000)
            return;
        var during = _startup.Elapsed < TimeSpan.FromSeconds(90) && count <= 80;
        Write($"{e.Exception.GetType().FullName}: {e.Exception.Message}" +
              (during ? Environment.NewLine + Environment.StackTrace : $" (in {e.Exception.TargetSite?.DeclaringType?.Name}.{e.Exception.TargetSite?.Name})"));
    }

    /// <summary>
    /// Notes a step in activity.log (a window change, a pane sliding, banners), so a crash inside WinUI,
    /// which leaves no exception of ours, shows what the app was doing just before.
    /// </summary>
    internal static void Trace(string step) => Write(step);

    // Each line is written straight through to the file (no buffer to lose when the process dies).
    private static void Write(string text)
    {
        try
        {
            lock (_logLock)
                File.AppendAllText(ActivityLog, $"{DateTime.Now:HH:mm:ss.fff} {LogText.Redact(text)}{Environment.NewLine}");
        }
        catch (Exception)
        {
            // The log must never be the reason the app fails.
        }
    }

    // Errors on the UI thread in a short while: past this many, the app is in trouble and ends after all.
    private static readonly Queue<DateTime> s_survived = new();

    /// <summary>
    /// An exception nobody caught on the UI thread (e.g. an animation WinUI refused). It goes into crash.log with the
    /// steps that led to it. The app carries on where it can, since photos and their details are safe in the files;
    /// several in a few seconds mean it can't, and then it ends as before.
    /// </summary>
    private void OnUnhandledException(object sender, Microsoft.UI.Xaml.UnhandledExceptionEventArgs e)
    {
        var now = DateTime.UtcNow;
        while (s_survived.Count > 0 && now - s_survived.Peek() > TimeSpan.FromSeconds(10))
            s_survived.Dequeue();
        // A lack of memory (another app took it) passes once that app lets go: the app frees what it can and carries
        // on, instead of ending and losing the user's place. It doesn't count towards the limit, since a memory-hungry
        // app next to it can cause a burst of them.
        var memory = Core.MemoryTrouble.IsMemoryFailure(e.Exception);
        var carryOn = memory || s_survived.Count < 3 && e.Exception is not (OutOfMemoryException or StackOverflowException or AccessViolationException);
        LogCrash(e.Exception, memory ? "Out of memory on the UI thread, the app frees memory and carries on"
                            : carryOn ? "Unhandled exception on the UI thread, the app carries on" : "Unhandled exception on the UI thread, the app ends",
                 e.Message);
        if (carryOn)
        {
            if (memory)
            {
                ViewModels.PhotoItemViewModel.ForgetHidden();
                Native.SystemMemory.TrimSoon();
            }
            else
                s_survived.Enqueue(now);
            e.Handled = true;
        }
    }

    // Keeps a record of crashes, since a WinUI app otherwise just disappears: what was thrown, its stack, and the
    // last steps of the activity log.
    internal static void LogCrash(object? exception, string what = "Crash", string? message = null)
    {
        try
        {
            var text = exception?.ToString() ?? "(no exception)";
            if (!string.IsNullOrEmpty(message) && !text.Contains(message, StringComparison.Ordinal))
                text = message + Environment.NewLine + text;
            Write($"{what}: {text}");
            Write(CrashWritten);
            string[] tail = [];
            try
            {
                lock (_logLock)
                    tail = File.ReadLines(ActivityLog).TakeLast(TailLines).ToArray();
            }
            catch (Exception)
            {
            }
            AppendCrash($"{what} (version {VersionText}, {_startup.Elapsed.TotalSeconds:0} s after start){Environment.NewLine}{text}" +
                        Environment.NewLine + "Last activity:" + Environment.NewLine + string.Join(Environment.NewLine, tail));
        }
        catch (Exception)
        {
        }
    }

    private static void AppendCrash(string text)
    {
        try
        {
            Directory.CreateDirectory(LibrarySettings.DefaultDataFolder);
            lock (_logLock)
                File.AppendAllText(CrashLog, $"===== {DateTime.Now:yyyy-MM-dd HH:mm:ss} {LogText.Redact(text)}{Environment.NewLine}{Environment.NewLine}");
        }
        catch (Exception)
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
            var window = new MainWindow();
            window.MaximizeIfRemembered();
            _window = window;
            _window.Activate();
        }
        catch (Exception ex)
        {
            // Errors while building the window bypass UnhandledException and end the app with no trace.
            LogCrash(ex, "Crash while building the window");
            throw;
        }
    }
}
