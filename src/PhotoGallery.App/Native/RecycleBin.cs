using System.Runtime.InteropServices;
using PhotoGallery.Core.Localization;

namespace PhotoGallery.App.Native;

/// <summary>Moves files to the Windows Recycle Bin instead of deleting them, so they can be restored.</summary>
internal static class RecycleBin
{
    private const uint FO_DELETE = 3;
    private const ushort FOF_SILENT = 0x4;
    private const ushort FOF_NOCONFIRMATION = 0x10;
    private const ushort FOF_ALLOWUNDO = 0x40;
    private const ushort FOF_NOERRORUI = 0x400;

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct ShFileOpStruct
    {
        public nint Window;
        public uint Func;
        public string From;
        public string? To;
        public ushort Flags;
        [MarshalAs(UnmanagedType.Bool)]
        public bool AnyOperationsAborted;
        public nint NameMappings;
        public string? ProgressTitle;
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern int SHFileOperation(ref ShFileOpStruct operation);

    /// <summary>Sends the file to the Recycle Bin without asking; a missing file is skipped.</summary>
    public static void Send(string path)
    {
        if (!File.Exists(path))
            return;
        var operation = new ShFileOpStruct
        {
            Func = FO_DELETE,
            From = Path.GetFullPath(path) + "\0\0", // a list of paths, ended by an empty one
            Flags = FOF_ALLOWUNDO | FOF_NOCONFIRMATION | FOF_SILENT | FOF_NOERRORUI,
        };
        var result = SHFileOperation(ref operation);
        if (result != 0 || operation.AnyOperationsAborted)
            throw new IOException(Strings.Tr("Windows could not move {0} to the Recycle Bin (error {1}).", Path.GetFileName(path), result));
    }
}
