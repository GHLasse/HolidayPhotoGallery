using System.Diagnostics;
using System.Text;
using static PhotoGallery.Core.Localization.Strings;

namespace PhotoGallery.Core.ExifTool;

/// <summary>
/// One long-running ExifTool process in "-stay_open" mode. Starting ExifTool costs far more than
/// running a command, so the whole app shares this process and sends it commands one at a time.
/// When the process dies (Perl ends itself with "Out of memory!" when another app has taken the memory)
/// or stops answering, the next command starts a new one, so the app recovers once memory is back
/// instead of every later read and save failing or waiting for ever.
/// </summary>
public sealed class ExifToolProcess : IAsyncDisposable, IDisposable
{
    private static readonly Encoding Utf8NoBom = new UTF8Encoding(false);

    private readonly ProcessStartInfo _startInfo;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private Process _process;
    private bool _broken; // the last command was cut off: the process's output can't be trusted any more
    private int _nextId;
    private bool _disposed;

    /// <summary>
    /// How long a command may go without ExifTool writing a single line before it counts as stuck. Long batches
    /// write a line per file, so only a process that hangs comes near it.
    /// </summary>
    public TimeSpan SilenceTimeout { get; set; } = TimeSpan.FromMinutes(3);

    /// <summary>How often the process had to be started again.</summary>
    public int Restarts { get; private set; }

    /// <summary>Says what went wrong and that ExifTool is started again, for the app's activity log.</summary>
    public event Action<string>? Trouble;

    public ExifToolProcess(string exifToolPath)
    {
        _startInfo = new ProcessStartInfo
        {
            FileName = exifToolPath,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardInputEncoding = Utf8NoBom,
            StandardOutputEncoding = Utf8NoBom,
            StandardErrorEncoding = Utf8NoBom,
        };
        // The config must come first; it teaches ExifTool the app's own XMP fields.
        _startInfo.ArgumentList.Add("-config");
        _startInfo.ArgumentList.Add(AppNamespace.WriteConfig());
        _startInfo.ArgumentList.Add("-stay_open");
        _startInfo.ArgumentList.Add("True");
        _startInfo.ArgumentList.Add("-@");
        _startInfo.ArgumentList.Add("-");

        _process = Start();
    }

    private Process Start() =>
        Process.Start(_startInfo) ?? throw new ExifToolException(Tr("Could not start ExifTool at {0}.", _startInfo.FileName));

    public static ExifToolProcess? TryStart()
    {
        var path = ExifToolLocator.Find();
        return path is null ? null : new ExifToolProcess(path);
    }

    /// <summary>
    /// Runs one ExifTool command. Each argument is passed as its own line, so values may contain
    /// spaces, quotes, non-ASCII text and line breaks.
    /// </summary>
    public async Task<ExifToolResult> RunAsync(IEnumerable<string> args, CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            EnsureRunning();

            var id = ++_nextId;
            var stdoutMarker = $"{{ready{id}}}";
            var stderrMarker = $"{{readyerr{id}}}";

            var sb = new StringBuilder();
            // Treat file names as UTF-8 so paths with umlauts and other non-ASCII characters work on Windows.
            sb.Append("-charset\nfilename=utf8\n");
            foreach (var arg in args)
                sb.Append(EncodeArgument(arg)).Append('\n');
            sb.Append("-echo4\n").Append(stderrMarker).Append('\n');
            sb.Append("-execute").Append(id).Append('\n');

            // From here on the command must be read to completion even if the caller cancels, or the next command
            // would read this one's output; if it can't be (the process died or hangs), the process is replaced.
            _broken = true;
            using var silence = new CancellationTokenSource(SilenceTimeout);
            try
            {
                await _process.StandardInput.WriteAsync(sb.ToString()).ConfigureAwait(false);
                await _process.StandardInput.FlushAsync(silence.Token).ConfigureAwait(false);

                // Read both streams to their markers concurrently so neither pipe can fill up and block ExifTool.
                var stdoutTask = ReadUntilAsync(_process.StandardOutput, stdoutMarker, silence);
                var stderrTask = ReadUntilAsync(_process.StandardError, stderrMarker, silence);
                await Task.WhenAll(stdoutTask, stderrTask).ConfigureAwait(false);

                _broken = false;
                return new ExifToolResult(stdoutTask.Result, stderrTask.Result);
            }
            catch (OperationCanceledException) when (silence.IsCancellationRequested)
            {
                Kill();
                var message = Tr("ExifTool stopped answering, so it is started again.");
                Trouble?.Invoke($"{message} ({SilenceTimeout.TotalSeconds:0} s without output)");
                throw new ExifToolException(message);
            }
            catch (Exception ex) when (ex is IOException or ObjectDisposedException or InvalidOperationException)
            {
                // The pipe broke: the process ended in the middle of the command.
                Trouble?.Invoke($"ExifTool ended during a command: {ex.Message}");
                throw new ExifToolException(Tr("ExifTool closed its output unexpectedly."));
            }
        }
        finally
        {
            _gate.Release();
        }
    }

    // A process that ended, or whose last command was cut off, is replaced by a new one.
    private void EnsureRunning()
    {
        bool exited;
        try
        {
            exited = _process.HasExited;
        }
        catch (InvalidOperationException)
        {
            exited = true;
        }
        if (!exited && !_broken)
            return;

        Trouble?.Invoke(exited ? $"ExifTool had ended (code {SafeExitCode()}); starting it again." : "ExifTool's last command was cut off; starting it again.");
        Kill();
        _process.Dispose();
        try
        {
            _process = Start();
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException or IOException or OutOfMemoryException)
        {
            // Tried again with the next command; until then the dead process stays in place, marked as such.
            throw new ExifToolException(Tr("Could not start ExifTool at {0}.", _startInfo.FileName) + " " + ex.Message);
        }
        _broken = false;
        Restarts++;
    }

    private string SafeExitCode()
    {
        try
        {
            return _process.ExitCode.ToString(System.Globalization.CultureInfo.InvariantCulture);
        }
        catch (InvalidOperationException)
        {
            return "?";
        }
    }

    private void Kill()
    {
        try
        {
            if (!_process.HasExited)
                _process.Kill(entireProcessTree: true);
        }
        catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception or NotSupportedException)
        {
            // Already gone.
        }
    }

    /// <summary>For tests: ends the process as if it had died, e.g. of a lack of memory.</summary>
    internal void KillForTest()
    {
        _process.Kill(entireProcessTree: true);
        _process.WaitForExit();
    }

    // ExifTool argument files are line based. A line starting with "#[CSTR]" is read as a C string,
    // which lets a value carry line breaks and backslashes safely.
    internal static string EncodeArgument(string arg)
    {
        if (arg.IndexOfAny(['\n', '\r', '\\', '\t']) < 0 && !arg.StartsWith('#'))
            return arg;

        var sb = new StringBuilder("#[CSTR]");
        foreach (var c in arg)
        {
            sb.Append(c switch
            {
                '\\' => "\\\\",
                '\n' => "\\n",
                '\r' => "\\r",
                '\t' => "\\t",
                _ => c.ToString(),
            });
        }
        return sb.ToString();
    }

    // Every line ExifTool writes shows it is still working, so the silence timer starts again.
    private async Task<string> ReadUntilAsync(StreamReader reader, string marker, CancellationTokenSource silence)
    {
        var sb = new StringBuilder();
        while (true)
        {
            var line = await reader.ReadLineAsync(silence.Token).ConfigureAwait(false)
                ?? throw new IOException("end of output");
            silence.CancelAfter(SilenceTimeout);
            if (line.TrimEnd() == marker)
                return sb.ToString();
            sb.Append(line).Append('\n');
        }
    }

    /// <summary>
    /// Stops ExifTool synchronously, for when the app is closing and cannot wait for async work:
    /// asks it to quit, and kills it (with its Perl child process) if it has not exited within two seconds.
    /// </summary>
    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;

        try
        {
            if (!_process.HasExited)
            {
                _process.StandardInput.Write("-stay_open\nFalse\n");
                _process.StandardInput.Flush();
                _process.StandardInput.Close();
                if (!_process.WaitForExit(2000))
                    _process.Kill(entireProcessTree: true);
            }
        }
        catch (Exception ex) when (ex is IOException or InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            // The process is already gone.
        }
        finally
        {
            _process.Dispose();
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed)
            return;
        _disposed = true;

        try
        {
            if (!_process.HasExited)
            {
                await _process.StandardInput.WriteAsync("-stay_open\nFalse\n").ConfigureAwait(false);
                await _process.StandardInput.FlushAsync().ConfigureAwait(false);
                using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
                try
                {
                    await _process.WaitForExitAsync(timeout.Token).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    _process.Kill(entireProcessTree: true);
                }
            }
        }
        catch (Exception ex) when (ex is IOException or InvalidOperationException)
        {
            // The process is already gone.
        }
        finally
        {
            _process.Dispose();
            _gate.Dispose();
        }
    }
}

public sealed record ExifToolResult(string Output, string Errors)
{
    public IEnumerable<string> ErrorLines =>
        Errors.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
              .Where(l => l.StartsWith("Error", StringComparison.OrdinalIgnoreCase));
}
