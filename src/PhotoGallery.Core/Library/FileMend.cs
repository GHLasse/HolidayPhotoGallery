using PhotoGallery.Core.Editing;
using static PhotoGallery.Core.Localization.Strings;

namespace PhotoGallery.Core.Library;

/// <summary>
/// Mends a file whose picture is complete but whose structure is not: a JPEG whose picture data is all there
/// but whose end mark is missing (a copy that stopped just short). The picture isn't touched, only the
/// end mark is added, and the file as it was is kept in the hidden .originals folder.
/// </summary>
public static class FileMend
{
    public static bool CanMend(string path) =>
        Path.GetExtension(path).ToLowerInvariant() is ".jpg" or ".jpeg" or ".jpe" && JpegScan.Check(path) is { MissingEnd: true, Damaged: false };

    /// <exception cref="IOException">The file can't be mended this way, or the mended file didn't check out.</exception>
    public static void Mend(string path)
    {
        if (File.GetAttributes(path).HasFlag(FileAttributes.ReadOnly))
            throw new IOException(Tr("Could not save {0}: the file is read-only. Use \"Make editable\" to unlock the photos in this folder.", Path.GetFileName(path)));
        if (JpegScan.Check(path) is not { MissingEnd: true, Damaged: false } scan)
            throw new IOException(Tr("{0} can't be mended this way: its picture data is not complete.", Path.GetFileName(path)));

        var temp = Path.Combine(Path.GetDirectoryName(path)!, $".mend-{Guid.NewGuid():N}{Path.GetExtension(path)}");
        try
        {
            using (var source = File.OpenRead(path))
            using (var target = File.Create(temp))
            {
                CopyBytes(source, target, scan.DataEnd);
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
