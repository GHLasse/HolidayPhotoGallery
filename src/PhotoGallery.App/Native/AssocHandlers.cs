using System.Runtime.InteropServices;
using ComDataObject = System.Runtime.InteropServices.ComTypes.IDataObject;

namespace PhotoGallery.App.Native;

/// <summary>An app Windows offers for a file type, as listed under "Open with" in Explorer.</summary>
/// <param name="Name">What identifies it: the program's path for desktop apps, an app id for Store apps.</param>
/// <param name="DisplayName">The name Windows shows, such as "Photos" or "Affinity Photo 2".</param>
/// <param name="IconPath">Where its icon is: a program or icon file, or a Store app resource.</param>
internal sealed record AssocApp(string Name, string DisplayName, string IconPath);

/// <summary>The apps Windows knows for a file type, and opening a file in one of them (Store apps included).</summary>
internal static class AssocHandlers
{
    private const int ASSOC_FILTER_RECOMMENDED = 1;
    private static readonly Guid BHID_DataObject = new("B8C0BD9F-ED24-455c-83E6-D5390C4FE8C4");
    private static readonly Guid IID_IDataObject = new("0000010e-0000-0000-C000-000000000046");
    private static readonly Guid IID_IShellItem = new("43826d1e-e718-42ee-bc55-a1e261c37bfe");

    [ComImport, Guid("973810ae-9599-4b88-9e4d-6ee98c9552da"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IEnumAssocHandlers
    {
        [PreserveSig]
        int Next(uint count, [Out, MarshalAs(UnmanagedType.LPArray, ArraySubType = UnmanagedType.Interface, SizeParamIndex = 0)] IAssocHandler[] handlers, out uint fetched);
    }

    [ComImport, Guid("F04061AC-1659-4a3f-A954-775AA57FC083"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IAssocHandler
    {
        [return: MarshalAs(UnmanagedType.LPWStr)]
        string GetName();

        [return: MarshalAs(UnmanagedType.LPWStr)]
        string GetUIName();

        void GetIconLocation([MarshalAs(UnmanagedType.LPWStr)] out string path, out int index);

        [PreserveSig]
        int IsRecommended();

        void MakeDefault([MarshalAs(UnmanagedType.LPWStr)] string description);

        void Invoke(ComDataObject dataObject);
    }

    [ComImport, Guid("43826d1e-e718-42ee-bc55-a1e261c37bfe"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IShellItem
    {
        [return: MarshalAs(UnmanagedType.Interface)]
        object BindToHandler(nint bindContext, ref Guid handler, ref Guid riid);
    }

    [ComImport, Guid("bcc18b79-ba16-442f-80c4-8a59c30c463b"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IShellItemImageFactory
    {
        void GetImage(NativeSize size, int flags, out nint bitmap);
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeSize(int cx, int cy)
    {
        public int Cx = cx, Cy = cy;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct BitmapHeader
    {
        public int Size, Width, Height;
        public short Planes, BitCount;
        public int Compression, SizeImage, XPelsPerMeter, YPelsPerMeter, ClrUsed, ClrImportant;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct BitmapInfo
    {
        public int Type, Width, Height, WidthBytes;
        public short Planes, BitsPixel;
        public nint Bits;
    }

    private static readonly Guid IID_IShellItemImageFactory = new("bcc18b79-ba16-442f-80c4-8a59c30c463b");
    private const int SIIGBF_BIGGERSIZEOK = 0x1, SIIGBF_ICONONLY = 0x4;

    [DllImport("gdi32.dll")]
    private static extern int GetObject(nint bitmap, int size, out BitmapInfo info);

    [DllImport("gdi32.dll")]
    private static extern int GetDIBits(nint dc, nint bitmap, uint start, uint lines, [Out] byte[] bits, ref BitmapHeader header, uint usage);

    [DllImport("gdi32.dll")]
    private static extern bool DeleteObject(nint handle);

    [DllImport("user32.dll")]
    private static extern nint GetDC(nint window);

    [DllImport("user32.dll")]
    private static extern int ReleaseDC(nint window, nint dc);

    [DllImport("shell32.dll", CharSet = CharSet.Unicode, PreserveSig = false)]
    private static extern IEnumAssocHandlers SHAssocEnumHandlers(string extension, int filter);

    [DllImport("shell32.dll", CharSet = CharSet.Unicode, PreserveSig = false)]
    [return: MarshalAs(UnmanagedType.Interface)]
    private static extern object SHCreateItemFromParsingName(string path, nint bindContext, ref Guid riid);

    /// <summary>The apps Windows recommends for the file type (".jpg"), by name. Runs off the UI thread.</summary>
    public static Task<List<AssocApp>> ListAsync(string extension) => RunOnStaThread(() =>
    {
        var apps = new List<AssocApp>();
        foreach (var handler in Enumerate(extension))
        {
            try
            {
                handler.GetIconLocation(out var icon, out _);
                var app = new AssocApp(handler.GetName(), handler.GetUIName(), icon ?? "");
                if (app.DisplayName.Length > 0 && !apps.Any(a => a.Name.Equals(app.Name, StringComparison.OrdinalIgnoreCase)))
                    apps.Add(app);
            }
            catch (Exception ex) when (ex is COMException or InvalidCastException)
            {
                // A broken registration: leave it out.
            }
        }
        return apps.OrderBy(a => a.DisplayName, StringComparer.CurrentCultureIgnoreCase).ToList();
    });

    /// <summary>Opens the file in the app, the way Explorer's "Open with" does. False when Windows no longer offers that app.</summary>
    public static Task<bool> OpenAsync(string appName, string path) => RunOnStaThread(() =>
    {
        // The app was chosen from the JPEG list; a HEIC or PNG photo may not list it, so that list is the fallback.
        var handler = Enumerate(Path.GetExtension(path)).Concat(Enumerate(".jpg"))
            .FirstOrDefault(h => h.GetName().Equals(appName, StringComparison.OrdinalIgnoreCase));
        if (handler is null)
            return false;
        Guid shellItem = IID_IShellItem, bindData = BHID_DataObject, dataObject = IID_IDataObject;
        var item = (IShellItem)SHCreateItemFromParsingName(path, 0, ref shellItem);
        var data = (ComDataObject)item.BindToHandler(0, ref bindData, ref dataObject);
        handler.Invoke(data);
        return true;
    });

    /// <summary>
    /// The app's icon as Explorer shows it, for a program's path or a Store app's id (shown through
    /// the shell's Apps folder, where packaged apps such as Affinity Photo from the Store live); null
    /// when Windows has none. Upright BGRA with straight alpha.
    /// </summary>
    public static Task<Imaging.DecodedImage?> IconAsync(string appName, int size) => RunOnStaThread(() =>
    {
        try
        {
            var parsing = Path.IsPathRooted(appName) ? appName : @"shell:AppsFolder\" + appName;
            Guid factoryId = IID_IShellItemImageFactory;
            var factory = (IShellItemImageFactory)SHCreateItemFromParsingName(parsing, 0, ref factoryId);
            factory.GetImage(new NativeSize(size, size), SIIGBF_ICONONLY | SIIGBF_BIGGERSIZEOK, out var bitmap);
            try
            {
                return Pixels(bitmap);
            }
            finally
            {
                DeleteObject(bitmap);
            }
        }
        catch (Exception ex) when (ex is COMException or InvalidCastException or FileNotFoundException or ArgumentException)
        {
            return null;
        }
    });

    /// <summary>A 32-bit bitmap's pixels, top row first, with the shell's premultiplied alpha undone.</summary>
    private static Imaging.DecodedImage? Pixels(nint bitmap)
    {
        if (GetObject(bitmap, Marshal.SizeOf<BitmapInfo>(), out var info) == 0 || info.Width <= 0 || info.Height == 0)
            return null;
        int width = info.Width, height = Math.Abs(info.Height);
        var header = new BitmapHeader
        {
            Size = Marshal.SizeOf<BitmapHeader>(), Width = width, Height = -height, Planes = 1, BitCount = 32,
        };
        var pixels = new byte[width * height * 4];
        var dc = GetDC(0);
        try
        {
            if (GetDIBits(dc, bitmap, 0, (uint)height, pixels, ref header, 0) == 0)
                return null;
        }
        finally
        {
            ReleaseDC(0, dc);
        }
        var anyAlpha = false;
        for (var i = 3; i < pixels.Length && !anyAlpha; i += 4)
            anyAlpha = pixels[i] != 0;
        for (var i = 0; i < pixels.Length; i += 4)
        {
            var a = pixels[i + 3];
            if (!anyAlpha)
                pixels[i + 3] = 255; // an old-style bitmap without transparency
            else if (a is > 0 and < 255)
                for (var c = 0; c < 3; c++)
                    pixels[i + c] = (byte)Math.Min(255, pixels[i + c] * 255 / a);
        }
        return new Imaging.DecodedImage(pixels, width, height);
    }

    private static IEnumerable<IAssocHandler> Enumerate(string extension)
    {
        IEnumAssocHandlers list;
        try
        {
            list = SHAssocEnumHandlers(extension, ASSOC_FILTER_RECOMMENDED);
        }
        catch (COMException)
        {
            yield break;
        }
        var one = new IAssocHandler[1];
        while (list.Next(1, one, out var fetched) == 0 && fetched == 1 && one[0] is { } handler)
            yield return handler;
    }

    /// <summary>
    /// Runs shell work on its own thread in a single-threaded apartment, as the shell expects, so it
    /// neither blocks nor re-enters the UI thread.
    /// </summary>
    private static Task<T> RunOnStaThread<T>(Func<T> work)
    {
        var done = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() =>
        {
            try
            {
                done.SetResult(work());
            }
            catch (Exception ex)
            {
                done.SetException(ex);
            }
        }) { IsBackground = true };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        return done.Task;
    }
}
