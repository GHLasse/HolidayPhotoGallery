using System.Runtime.InteropServices;
using ComTypes = System.Runtime.InteropServices.ComTypes;

namespace PhotoGallery.App.Native;

/// <summary>
/// Phones and cameras as File Explorer shows them, under "This PC" (MTP, without a drive letter) or next to it, reached
/// through the shell the way Explorer reaches them. Windows' storage API for such devices
/// (Windows.Devices.Portable) needs a packaged app with the removable storage capability, which this app
/// isn't, and Windows' import API doesn't list every phone. Every call runs on its own STA thread, as the
/// shell's portable device folders expect.
/// </summary>
internal static class ShellDevices
{
    internal sealed record Device(string Name, string ParsingName);

    internal sealed record DeviceFile(string Name, long Size, DateTime Taken, string ParsingName, string Folder);

    /// <summary>
    /// The phones and cameras File Explorer shows: under "This PC" (MTP, no file system path), and next to it
    /// at the top of Explorer's tree, where Windows 11 puts a phone (Mobile devices, Phone Link). A top level
    /// entry counts when it holds a photo folder (DCIM, Pictures) in it or one level down.
    /// </summary>
    public static Task<List<Device>> FindAsync(IReadOnlyCollection<string> photoFolders, Action<string>? log = null) => OnSta(() =>
    {
        var result = new List<Device>();
        Guid folder = FOLDERID_ComputerFolder, iid = IID_IShellItem;
        SHGetKnownFolderItem(ref folder, 0, IntPtr.Zero, ref iid, out var computerObject);
        var computer = (IShellItem)computerObject;
        foreach (var item in Children(computer))
        {
            item.GetAttributes(SFGAO_FILESYSTEM | SFGAO_FOLDER, out var attributes);
            var parsing = Name(item, SIGDN_DESKTOPABSOLUTEPARSING);
            log?.Invoke($"This PC: {Name(item, SIGDN_NORMALDISPLAY)} · attributes {attributes:X8} · {parsing}");
            if ((attributes & SFGAO_FILESYSTEM) == 0 && LooksLikeDevice(parsing))
                result.Add(new Device(Name(item, SIGDN_NORMALDISPLAY), parsing));
        }

        // The top of Explorer's tree (the desktop of the shell's namespace, an empty ID list).
        var empty = Marshal.AllocCoTaskMem(2);
        try
        {
            Marshal.WriteInt16(empty, 0);
            SHCreateItemFromIDList(empty, ref iid, out var rootObject);
            foreach (var item in Children((IShellItem)rootObject))
            {
                try
                {
                    item.GetAttributes(SFGAO_FILESYSTEM | SFGAO_FOLDER, out var attributes);
                    var parsing = Name(item, SIGDN_DESKTOPABSOLUTEPARSING);
                    // Folders on the disk (a phone in Windows' Mobile devices is found by its folder instead),
                    // files on the desktop and Explorer's own places are passed over without a word.
                    if ((attributes & SFGAO_FILESYSTEM) != 0 || (attributes & SFGAO_FOLDER) == 0 || IsKnownPlace(parsing) ||
                        result.Any(d => string.Equals(d.ParsingName, parsing, StringComparison.OrdinalIgnoreCase)))
                        continue;
                    var name = Name(item, SIGDN_NORMALDISPLAY);
                    log?.Invoke($"Top level: {name} · attributes {attributes:X8} · {parsing}");
                    if (HoldsPhotoFolder(item, photoFolders, log))
                        result.Add(new Device(name, parsing));
                }
                catch (Exception ex) when (ex is COMException or InvalidCastException or UnauthorizedAccessException)
                {
                    log?.Invoke($"Top level entry skipped: {ex.Message}");
                }
            }
        }
        finally
        {
            Marshal.FreeCoTaskMem(empty);
        }
        return result;
    });

    // Explorer's own places at the top of its tree, never a phone: This PC, Network, Control Panel, Recycle
    // Bin, Libraries, Home, Gallery, the user's folder, OneDrive, Linux.
    private static readonly string[] KnownPlaces =
    [
        "20D04FE0-3AEA-1069-A2D8-08002B30309D", "F02C1A0D-BE21-4350-88B0-7367FC96EF3C", "26EE0668-A00A-44D7-9371-BEB064C98683",
        "5399E694-6CE5-4D6C-8FCE-1D8870FDCBA0", "21EC2020-3AEA-1069-A2DD-08002B30309D", "645FF040-5081-101B-9F08-00AA002F954E",
        "031E4825-7B94-4DC3-B131-E946B44C8DD5", "F874310E-B6B7-47DC-BC84-B9E6B38F5903", "E88865EA-0E1C-4E20-9AA6-EDCD0212C87C",
        "59031A47-3F72-44A7-89C5-5595FE6B30EE", "018D5C66-4533-4307-9B53-224DE2ED1FE6", "B4FB3F98-C1EA-428D-A78A-D1F5659CBA93",
        "679F85CB-0220-4080-B29B-5540CC05AAB6", "4234D49B-0245-4DF3-B780-3893943456E1", "B2B4A4D1-2754-4140-A2EB-9A76D9D7CDC6",
    ];

    private static bool IsKnownPlace(string parsing) =>
        KnownPlaces.Any(id => parsing.Contains(id, StringComparison.OrdinalIgnoreCase));

    private static bool HoldsPhotoFolder(IShellItem device, IReadOnlyCollection<string> photoFolders, Action<string>? log)
    {
        var children = Children(device);
        if (children.Any(c => photoFolders.Contains(FileName(c), StringComparer.OrdinalIgnoreCase)))
            return true;
        foreach (var storage in children.Take(4))
        {
            try
            {
                if (Children(storage).Any(c => photoFolders.Contains(FileName(c), StringComparer.OrdinalIgnoreCase)))
                    return true;
            }
            catch (Exception ex) when (ex is COMException or InvalidCastException or UnauthorizedAccessException)
            {
                log?.Invoke($"  {FileName(storage)}: {ex.Message}");
            }
        }
        return false;
    }

    /// <summary>
    /// The files on the device that <paramref name="wanted"/> accepts (by file name), in the folders named in
    /// <paramref name="photoFolders"/> of each storage ("Internal shared storage", a card), or in the whole
    /// storage when it has none of them.
    /// </summary>
    public static Task<List<DeviceFile>> ListFilesAsync(string device, IReadOnlyCollection<string> photoFolders, Func<string, bool> wanted,
                                                        CancellationToken cancel = default) => OnSta(() =>
    {
        var files = new List<DeviceFile>();
        // The date taken has to be read from the file itself over the cable, which is slow and which many phones
        // don't offer: it is only asked for while the first files have one.
        var askTaken = 20;
        void Walk(IShellItem folder, string path, int depth)
        {
            foreach (var item in Children(folder))
            {
                cancel.ThrowIfCancellationRequested();
                item.GetAttributes(SFGAO_FOLDER | SFGAO_STREAM, out var attributes);
                var name = FileName(item);
                if ((attributes & SFGAO_FOLDER) != 0 && (attributes & SFGAO_STREAM) == 0)
                {
                    // Thumbnail caches and trash aren't photos to import.
                    if (depth < 8 && !name.StartsWith('.'))
                        Walk(item, path + "/" + name, depth + 1);
                }
                else if (wanted(name))
                {
                    var file = (IShellItem2)item;
                    var size = TryUInt64(file, PKEY_Size);
                    DateTime? taken = null;
                    if (askTaken > 0)
                    {
                        taken = TryTime(file, PKEY_DateTaken);
                        askTaken = taken is null ? askTaken - 1 : 20;
                    }
                    taken ??= TryTime(file, PKEY_DateModified) ?? TryTime(file, PKEY_DateCreated) ?? DateTime.Now;
                    files.Add(new DeviceFile(name, (long)(size ?? 0), taken.Value, Name(item, SIGDN_DESKTOPABSOLUTEPARSING), path));
                }
            }
        }
        var top = Children(Parse(device));
        // A phone that shows its photo folders straight away (Windows 11's Mobile devices) is read from them.
        var direct = top.Where(f => photoFolders.Contains(FileName(f), StringComparer.OrdinalIgnoreCase)).ToList();
        foreach (var folder in direct)
            Walk(folder, FileName(folder), 0);
        foreach (var storage in direct.Count > 0 ? new List<IShellItem>() : top)
        {
            var folders = Children(storage).Where(f => photoFolders.Contains(FileName(f), StringComparer.OrdinalIgnoreCase)).ToList();
            // A camera's storage holds DCIM; something else is looked through whole.
            if (folders.Count == 0)
                Walk(storage, "", 0);
            foreach (var folder in folders)
                Walk(folder, FileName(folder), 0);
        }
        return files;
    });

    /// <summary>Copies a file from the device to <paramref name="target"/>; returns the bytes copied.</summary>
    public static Task<long> CopyAsync(string file, string target) => OnSta(() =>
    {
        Guid bind = BHID_Stream, iid = IID_IStream;
        Parse(file).BindToHandler(IntPtr.Zero, ref bind, ref iid, out var streamObject);
        var stream = (ComTypes.IStream)streamObject;
        try
        {
            var buffer = new byte[1 << 20];
            var readPointer = Marshal.AllocCoTaskMem(sizeof(int));
            long total = 0;
            try
            {
                using var output = new FileStream(target, FileMode.Create, FileAccess.Write, FileShare.None);
                while (true)
                {
                    stream.Read(buffer, buffer.Length, readPointer);
                    var read = Marshal.ReadInt32(readPointer);
                    if (read <= 0)
                        break;
                    output.Write(buffer, 0, read);
                    total += read;
                }
            }
            finally
            {
                Marshal.FreeCoTaskMem(readPointer);
            }
            return total;
        }
        finally
        {
            Marshal.ReleaseComObject(stream);
        }
    });

    /// <summary>Deletes a file from the device (a phone has no Recycle Bin), without asking.</summary>
    public static Task DeleteAsync(string file) => OnSta(() =>
    {
        var operation = (IFileOperation)Activator.CreateInstance(Type.GetTypeFromCLSID(CLSID_FileOperation)!)!;
        try
        {
            operation.SetOperationFlags(FOF_SILENT | FOF_NOCONFIRMATION | FOF_NOERRORUI);
            operation.DeleteItem(Parse(file), IntPtr.Zero);
            operation.PerformOperations();
        }
        finally
        {
            Marshal.ReleaseComObject(operation);
        }
        return true;
    });

    // A portable device's parsing name holds its device path: \\?\usb#…, \\?\swd#wpdbusenum…, or the WPD device class.
    internal static bool LooksLikeDevice(string parsing) =>
        parsing.Contains(@"\\?\", StringComparison.Ordinal) ||
        parsing.Contains("usb#", StringComparison.OrdinalIgnoreCase) ||
        parsing.Contains("wpdbusenum", StringComparison.OrdinalIgnoreCase) ||
        parsing.Contains("6ac27878-a6fa-4155-ba85-f98f491d4f33", StringComparison.OrdinalIgnoreCase);

    private static Task<T> OnSta<T>(Func<T> work)
    {
        var done = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() =>
        {
            try
            {
                // No "device not ready" message boxes from Windows on this thread: a failure comes back as an error.
                SetThreadErrorMode(SEM_FAILCRITICALERRORS | SEM_NOOPENFILEERRORBOX, out _);
                done.SetResult(work());
            }
            catch (Exception ex)
            {
                done.SetException(ex);
            }
        }) { IsBackground = true, Name = "Shell devices" };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        return done.Task;
    }

    private static IShellItem Parse(string parsingName)
    {
        Guid iid = IID_IShellItem;
        return (IShellItem)SHCreateItemFromParsingName(parsingName, IntPtr.Zero, ref iid);
    }

    private static List<IShellItem> Children(IShellItem folder)
    {
        var result = new List<IShellItem>();
        Guid bind = BHID_EnumItems, iid = IID_IEnumShellItems;
        folder.BindToHandler(IntPtr.Zero, ref bind, ref iid, out var enumObject);
        var items = (IEnumShellItems)enumObject;
        int hr;
        while ((hr = items.Next(1, out var item, out var fetched)) == 0 && fetched == 1)
            result.Add(item);
        // A phone unplugged or switched to another USB mode midway fails here: stop rather than go on folder
        // after folder, each failing again.
        if (hr < 0)
            Marshal.ThrowExceptionForHR(hr);
        return result;
    }

    private static string Name(IShellItem item, uint form)
    {
        item.GetDisplayName(form, out var pointer);
        try
        {
            return Marshal.PtrToStringUni(pointer) ?? "";
        }
        finally
        {
            Marshal.FreeCoTaskMem(pointer);
        }
    }

    // The real file name with its extension (the display name can hide it, as Explorer's setting says).
    private static string FileName(IShellItem item)
    {
        try
        {
            var key = PKEY_FileName;
            ((IShellItem2)item).GetString(ref key, out var pointer);
            try
            {
                if (Marshal.PtrToStringUni(pointer) is { Length: > 0 } name)
                    return name;
            }
            finally
            {
                Marshal.FreeCoTaskMem(pointer);
            }
        }
        catch (Exception ex) when (ex is COMException or InvalidCastException)
        {
        }
        return Name(item, SIGDN_NORMALDISPLAY);
    }

    private static ulong? TryUInt64(IShellItem2 item, PropertyKey key)
    {
        try
        {
            item.GetUInt64(ref key, out var value);
            return value;
        }
        catch (COMException)
        {
            return null;
        }
    }

    private static DateTime? TryTime(IShellItem2 item, PropertyKey key)
    {
        try
        {
            item.GetFileTime(ref key, out var time);
            var ticks = ((long)(uint)time.dwHighDateTime << 32) | (uint)time.dwLowDateTime;
            return ticks > 0 ? DateTime.FromFileTimeUtc(ticks).ToLocalTime() : null;
        }
        catch (Exception ex) when (ex is COMException or ArgumentOutOfRangeException)
        {
            return null;
        }
    }

    private const uint SFGAO_STREAM = 0x00400000, SFGAO_FOLDER = 0x20000000, SFGAO_FILESYSTEM = 0x40000000;
    private const uint SIGDN_NORMALDISPLAY = 0, SIGDN_DESKTOPABSOLUTEPARSING = 0x80028000;
    private const uint FOF_SILENT = 0x4, FOF_NOCONFIRMATION = 0x10, FOF_NOERRORUI = 0x400;

    private static readonly Guid FOLDERID_ComputerFolder = new("0AC0837C-BBF8-452A-850D-79D08E667CA7");
    private static readonly Guid BHID_EnumItems = new("94f60519-2850-4924-aa5a-d15e84868039");
    private static readonly Guid BHID_Stream = new("1cebb3ab-7c10-499a-a417-92ca16c4cb83");
    private static readonly Guid IID_IShellItem = new("43826d1e-e718-42ee-bc55-a1e261c37bfe");
    private static readonly Guid IID_IEnumShellItems = new("70629033-e363-4a28-a567-0db78006e6d7");
    private static readonly Guid IID_IStream = new("0000000c-0000-0000-C000-000000000046");
    private static readonly Guid CLSID_FileOperation = new("3ad05575-8857-4850-9277-11b85bdb8e09");

    private static readonly PropertyKey PKEY_FileName = new(new Guid("41CF5AE0-F75A-4806-BD87-59C7D9248EB9"), 100);
    private static readonly PropertyKey PKEY_Size = new(new Guid("B725F130-47EF-101A-A5F1-02608C9EEBAC"), 12);
    private static readonly PropertyKey PKEY_DateModified = new(new Guid("B725F130-47EF-101A-A5F1-02608C9EEBAC"), 14);
    private static readonly PropertyKey PKEY_DateCreated = new(new Guid("B725F130-47EF-101A-A5F1-02608C9EEBAC"), 15);
    private static readonly PropertyKey PKEY_DateTaken = new(new Guid("14B81DA1-0135-4D31-96D9-6CBFC9671A99"), 36867);

    [StructLayout(LayoutKind.Sequential)]
    private readonly struct PropertyKey(Guid format, uint id)
    {
        public readonly Guid Format = format;
        public readonly uint Id = id;
    }

    private const uint SEM_FAILCRITICALERRORS = 0x1, SEM_NOOPENFILEERRORBOX = 0x8000;

    [DllImport("kernel32.dll")]
    private static extern bool SetThreadErrorMode(uint mode, out uint oldMode);

    [DllImport("shell32.dll", PreserveSig = false)]
    private static extern void SHGetKnownFolderItem(ref Guid folder, int flags, IntPtr token, ref Guid iid,
                                                    [MarshalAs(UnmanagedType.Interface)] out object item);

    [DllImport("shell32.dll", PreserveSig = false)]
    private static extern void SHCreateItemFromIDList(IntPtr idList, ref Guid iid, [MarshalAs(UnmanagedType.Interface)] out object item);

    [DllImport("shell32.dll", CharSet = CharSet.Unicode, PreserveSig = false)]
    [return: MarshalAs(UnmanagedType.Interface)]
    private static extern object SHCreateItemFromParsingName(string path, IntPtr bindContext, ref Guid iid);

    [ComImport, Guid("43826d1e-e718-42ee-bc55-a1e261c37bfe"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IShellItem
    {
        void BindToHandler(IntPtr bindContext, ref Guid handler, ref Guid iid, [MarshalAs(UnmanagedType.Interface)] out object result);
        void GetParent(out IShellItem parent);
        void GetDisplayName(uint form, out IntPtr name);
        void GetAttributes(uint mask, out uint attributes);
        void Compare(IShellItem other, uint hint, out int order);
    }

    [ComImport, Guid("7e9fb0d3-919f-4307-ab2e-9b1860310c93"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IShellItem2 : IShellItem
    {
        new void BindToHandler(IntPtr bindContext, ref Guid handler, ref Guid iid, [MarshalAs(UnmanagedType.Interface)] out object result);
        new void GetParent(out IShellItem parent);
        new void GetDisplayName(uint form, out IntPtr name);
        new void GetAttributes(uint mask, out uint attributes);
        new void Compare(IShellItem other, uint hint, out int order);
        void GetPropertyStore(int flags, ref Guid iid, out IntPtr store);
        void GetPropertyStoreWithCreateObject(int flags, IntPtr createObject, ref Guid iid, out IntPtr store);
        void GetPropertyStoreForKeys(IntPtr keys, uint count, int flags, ref Guid iid, out IntPtr store);
        void GetPropertyDescriptionList(ref PropertyKey type, ref Guid iid, out IntPtr list);
        void Update(IntPtr bindContext);
        void GetProperty(ref PropertyKey key, IntPtr value);
        void GetCLSID(ref PropertyKey key, out Guid clsid);
        void GetFileTime(ref PropertyKey key, out ComTypes.FILETIME time);
        void GetInt32(ref PropertyKey key, out int value);
        void GetString(ref PropertyKey key, out IntPtr value);
        void GetUInt32(ref PropertyKey key, out uint value);
        void GetUInt64(ref PropertyKey key, out ulong value);
        void GetBool(ref PropertyKey key, out int value);
    }

    [ComImport, Guid("70629033-e363-4a28-a567-0db78006e6d7"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IEnumShellItems
    {
        [PreserveSig]
        int Next(uint count, out IShellItem item, out uint fetched);
        void Skip(uint count);
        void Reset();
        void Clone(out IEnumShellItems copy);
    }

    // Only DeleteItem, SetOperationFlags and PerformOperations are called; the others keep the slots in order.
    [ComImport, Guid("947aab5f-0a5c-4c13-b4d6-4bf7836fc9f8"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IFileOperation
    {
        void Advise(IntPtr sink, out uint cookie);
        void Unadvise(uint cookie);
        void SetOperationFlags(uint flags);
        void SetProgressMessage([MarshalAs(UnmanagedType.LPWStr)] string message);
        void SetProgressDialog(IntPtr dialog);
        void SetProperties(IntPtr properties);
        void SetOwnerWindow(IntPtr owner);
        void ApplyPropertiesToItem(IShellItem item);
        void ApplyPropertiesToItems(IntPtr items);
        void RenameItem(IShellItem item, [MarshalAs(UnmanagedType.LPWStr)] string newName, IntPtr sink);
        void RenameItems(IntPtr items, [MarshalAs(UnmanagedType.LPWStr)] string newName);
        void MoveItem(IShellItem item, IShellItem destination, [MarshalAs(UnmanagedType.LPWStr)] string newName, IntPtr sink);
        void MoveItems(IntPtr items, IShellItem destination);
        void CopyItem(IShellItem item, IShellItem destination, [MarshalAs(UnmanagedType.LPWStr)] string newName, IntPtr sink);
        void CopyItems(IntPtr items, IShellItem destination);
        void DeleteItem(IShellItem item, IntPtr sink);
        void DeleteItems(IntPtr items);
        void NewItem(IShellItem destination, uint attributes, [MarshalAs(UnmanagedType.LPWStr)] string name,
                     [MarshalAs(UnmanagedType.LPWStr)] string template, IntPtr sink);
        void PerformOperations();
        void GetAnyOperationsAborted(out int aborted);
    }
}
