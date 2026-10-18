using System.Diagnostics;
using System.Text;

namespace PhotoGallery.Core.ExifTool;

/// <summary>
/// One long-running ExifTool process in "-stay_open" mode. Starting ExifTool costs far more than
/// running a command, so the whole app shares this process and sends it commands one at a time.
/// </summary>
public sealed class ExifToolProcess : IAsyncDisposable, IDisposable
{
    private static readonly Encoding Utf8NoBom = new UTF8Encoding(false);

    private readonly Process _process;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private int _nextId;
    private bool _disposed;

    public ExifToolProcess(string exifToolPath)
    {
        var psi = new ProcessStartInfo
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
        psi.ArgumentList.Add("-stay_open");
        psi.ArgumentList.Add("True");
        psi.ArgumentList.Add("-@");
        psi.ArgumentList.Add("-");

        _process = Process.Start(psi) ?? throw new ExifToolException($"Could not start ExifTool at {exifToolPath}.");
    }

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
            if (_process.HasExited)
                throw new ExifToolException($"ExifTool exited unexpectedly (code {_process.ExitCode}).");

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

            await _process.StandardInput.WriteAsync(sb.ToString()).ConfigureAwait(false);
            await _process.StandardInput.FlushAsync(cancellationToken).ConfigureAwait(false);

            // Read both streams to their markers concurrently so neither pipe can fill up and block ExifTool.
            // Once a command is sent it must be read to completion even if the caller cancels, or the next
            // command would read this one's output.
            var stdoutTask = ReadUntilAsync(_process.StandardOutput, stdoutMarker);
            var stderrTask = ReadUntilAsync(_process.StandardError, stderrMarker);
            await Task.WhenAll(stdoutTask, stderrTask).ConfigureAwait(false);

            return new ExifToolResult(stdoutTask.Result, stderrTask.Result);
        }
        finally
        {
            _gate.Release();
        }
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

    private static async Task<string> ReadUntilAsync(StreamReader reader, string marker)
    {
        var sb = new StringBuilder();
        while (true)
        {
            var line = await reader.ReadLineAsync().ConfigureAwait(false)
                ?? throw new ExifToolException("ExifTool closed its output unexpectedly.");
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
