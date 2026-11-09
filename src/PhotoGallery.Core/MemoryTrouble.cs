namespace PhotoGallery.Core;

/// <summary>
/// Tells a lack of memory (or of graphics memory) apart from a photo that simply can't be read. When another app
/// takes most of the memory, Windows' codecs, the thumbnail cache and the graphics card fail for a while; that is
/// worth waiting out and trying again, while a damaged file is not.
/// </summary>
public static class MemoryTrouble
{
    // HRESULTs Windows' codecs, storage and graphics give when memory (or video memory) runs out.
    private static readonly HashSet<int> MemoryCodes =
    [
        unchecked((int)0x8007000E), // E_OUTOFMEMORY
        unchecked((int)0x80070008), // ERROR_NOT_ENOUGH_MEMORY
        unchecked((int)0x800705AF), // ERROR_COMMITMENT_LIMIT (page file full)
        unchecked((int)0x800705AA), // ERROR_NO_SYSTEM_RESOURCES
        unchecked((int)0x800705AD), // ERROR_WORKING_SET_QUOTA
        unchecked((int)0x887A0005), // DXGI_ERROR_DEVICE_REMOVED
        unchecked((int)0x887A0006), // DXGI_ERROR_DEVICE_HUNG
        unchecked((int)0x887A0007), // DXGI_ERROR_DEVICE_RESET
        unchecked((int)0x8899000C), // D2DERR_RECREATE_TARGET
        unchecked((int)0x88980406), // UCEERR_RENDERTHREADFAILURE
    ];

    /// <summary>True when <paramref name="exception"/> (or one it wraps) comes from a lack of memory.</summary>
    public static bool IsMemoryFailure(Exception? exception)
    {
        for (var depth = 0; exception is not null && depth < 8; depth++)
        {
            if (exception is OutOfMemoryException or InsufficientMemoryException || MemoryCodes.Contains(exception.HResult))
                return true;
            if (exception is AggregateException aggregate)
                return aggregate.InnerExceptions.Any(IsMemoryFailure);
            exception = exception.InnerException;
        }
        return false;
    }

    /// <summary>
    /// A lack of memory, or a wait that timed out (a helper process stuck while the PC was swapping): either passes,
    /// so the work is tried again later.
    /// </summary>
    public static bool IsPassing(Exception? exception) => exception is TimeoutException || IsMemoryFailure(exception);

    /// <summary>How long to wait before the <paramref name="attempt"/>th try again (1, 2, ...): 2 s, doubling, at most a minute.</summary>
    public static TimeSpan RetryDelay(int attempt) =>
        TimeSpan.FromSeconds(Math.Min(60, 2 * Math.Pow(2, Math.Clamp(attempt, 1, 10) - 1)));

    /// <summary>
    /// The PC's memory is nearly full (<paramref name="memoryLoadPercent"/> at or above <paramref name="threshold"/>),
    /// so work that can wait, waits. An unknown load (negative) never holds anything up.
    /// </summary>
    public static bool IsLow(int memoryLoadPercent, int threshold = 92) => memoryLoadPercent >= threshold;
}
