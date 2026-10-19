using System.Runtime.InteropServices;

namespace PhotoGallery.App.Native;

/// <summary>Windows' own "How do you want to open this file?" dialog, as Explorer shows it for "Open with".</summary>
internal static class OpenWithDialog
{
    private const int OAIF_EXEC = 0x4;
    private const int ErrorCancelled = unchecked((int)0x800704C7);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct OpenAsInfo
    {
        public string File;
        public string? Class;
        public int Flags;
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern int SHOpenWithDialog(nint owner, ref OpenAsInfo info);

    /// <summary>
    /// Lets the user pick an app and opens the file in it. Returns false when they cancelled. Without
    /// OAIF_ALLOW_REGISTRATION the dialog can't change which app opens photos by default.
    /// Runs off the UI thread, since the dialog blocks until it closes.
    /// </summary>
    public static Task<bool> ShowAsync(nint owner, string path) => Task.Run(() =>
    {
        var info = new OpenAsInfo { File = path, Flags = OAIF_EXEC };
        var result = SHOpenWithDialog(owner, ref info);
        if (result == ErrorCancelled)
            return false;
        Marshal.ThrowExceptionForHR(result);
        return true;
    });
}
