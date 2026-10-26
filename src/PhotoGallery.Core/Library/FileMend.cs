using PhotoGallery.Core.Editing;
using static PhotoGallery.Core.Localization.Strings;

namespace PhotoGallery.Core.Library;

/// <summary>
/// Mends a file whose picture is complete but whose structure is not: a JPEG whose picture data is all there
/// but whose end mark is missing (a copy that stopped just short). The picture isn't touched, only the
/// end mark is added, and the file as it was is kept in the hidden .originals folder. For a progressive JPEG,
/// whose data can't be followed, the end mark is added if the file then checks out: such a file stopped in its
/// last pass, which only adds fine detail, so the picture looks the same.
/// </summary>
public static class FileMend
{
    public static bool CanMend(string path) =>
        Path.GetExtension(path).ToLowerInvariant() is ".jpg" or ".jpeg" or ".jpe" && MendedLength(path) is not null;

    // How much of the file to keep before the end mark, or null when adding one doesn't make the file whole.
    private static long? MendedLength(string path)
    {
        var scan = JpegScan.Check(path);
        if (scan is { MissingEnd: true, Damaged: false })
            return scan.DataEnd;
        if (scan is not null)
            return null;
        using var file = File.OpenRead(path);
        if (FileCheck.Check(file, ".jpg") != FileHealth.CutOff)
            return null;
        var length = file.Length;
        file.Position = length - 1;
        if (file.ReadByte() == 0xFF)
            length--; // a marker cut after its first byte
        var mended = new MemoryStream();
        file.Position = 0;
        CopyBytes(file, mended, length);
        mended.Write([0xFF, 0xD9]);
        mended.Position = 0;
        return FileCheck.Check(mended, ".jpg") == FileHealth.Whole ? length : null;
    }

    /// <exception cref="IOException">The file can't be mended this way, or the mended file didn't check out.</exception>
    public static void Mend(string path)
    {
        if (File.GetAttributes(path).HasFlag(FileAttributes.ReadOnly))
            throw new IOException(Tr("Could not save {0}: the file is read-only. Use \"Make editable\" to unlock the photos in this folder.", Path.GetFileName(path)));
        if (MendedLength(path) is not { } keep)
            throw new IOException(Tr("{0} can't be mended this way: its picture data is not complete.", Path.GetFileName(path)));

        var temp = Path.Combine(Path.GetDirectoryName(path)!, $".mend-{Guid.NewGuid():N}{Path.GetExtension(path)}");
        try
        {
            using (var source = File.OpenRead(path))
            using (var target = File.Create(temp))
            {
                CopyBytes(source, target, keep);
                target.Write([0xFF, 0xD9]);
            }
            if (FileCheck.Check(temp) != FileHealth.Whole)
                throw new IOException(Tr("{0} can't be mended this way: its picture data is not complete.", Path.GetFileName(path)));
            EditHistory.BackUp(path);
            File.Move(temp, path, overwrite: true);
        }
        finally
        {
            if (File.Exists(temp))
                File.Delete(temp);
        }
    }

    private static void CopyBytes(Stream source, Stream target, long count)
    {
        var buffer = new byte[1 << 16];
        while (count > 0)
        {
            var read = source.Read(buffer, 0, (int)Math.Min(buffer.Length, count));
            if (read <= 0)
                break;
            target.Write(buffer, 0, read);
            count -= read;
        }
    }
}
