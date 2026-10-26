using PhotoGallery.Core.Cloud;
using PhotoGallery.Core.Editing;

namespace PhotoGallery.Core.Library;

/// <summary>
/// Moves or copies photos into another folder, as dragging them onto a folder or album does. Their edit
/// backups go with them, and a moved photo keeps its cloud marks.
/// </summary>
public static class PhotoMover
{
    /// <summary>Moves or copies the photo into the folder and returns its new path; a name already taken there gets " (2)" and so on.</summary>
    public static string Transfer(string path, string folder, bool copy)
    {
        var target = FreeName(folder, Path.GetFileName(path));
        if (copy)
            File.Copy(path, target);
        else
            File.Move(path, target);
        EditHistory.Transfer(path, target, copy);
        if (!copy)
            UploadFile.Carry(path, target);
        return target;
    }

    /// <summary>
    /// Renames photos in their folders (F-2). Each one takes along its edit backups, cloud marks, sidecar files
    /// (IMG_1.xmp, IMG_1.jpg.xmp, IMG_1.aae), its place as album cover and its index entry with its faces. Goes
    /// through temporary names first, so photos can swap names. Returns the renames that happened, old path to new;
    /// <paramref name="failed"/> gets the photos that could not be renamed and why.
    /// </summary>
    public static List<(IndexedPhoto Photo, string NewPath)> Rename(IReadOnlyList<(IndexedPhoto Photo, string NewName)> plan, PhotoIndex index,
                                                                    List<string> failed)
    {
        var steps = new List<(IndexedPhoto Photo, string Temp, string Target, List<(string Temp, Func<string, string> Final)> Sidecars)>();
        foreach (var (photo, newName) in plan)
        {
            var target = Path.Combine(Path.GetDirectoryName(photo.Path)!, newName);
            if (string.Equals(target, photo.Path, StringComparison.Ordinal))
                continue;
            var temp = photo.Path + ".renaming-" + Guid.NewGuid().ToString("N")[..8];
            try
            {
                MoveWithEverything(photo.Path, temp, index, photo);
                // The sidecars step aside too, so photos swapping names don't take each other's.
                var sidecars = new List<(string, Func<string, string>)>();
                foreach (var (sidecar, final) in Sidecars(photo.Path))
                {
                    var aside = temp + ".sidecar" + sidecars.Count;
                    File.Move(sidecar, aside);
                    sidecars.Add((aside, final));
                }
                steps.Add((photo, temp, target, sidecars));
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                failed.Add($"{photo.FileName}: {ex.Message}");
            }
        }
        var done = new List<(IndexedPhoto, string)>();
        foreach (var (photo, temp, target, sidecars) in steps)
        {
            var landed = photo.Path;
            try
            {
                if (File.Exists(target) && !string.Equals(target, photo.Path, StringComparison.OrdinalIgnoreCase))
                    throw new IOException($"{Path.GetFileName(target)} exists");
                MoveWithEverything(temp, target, index, photo.MovedTo(temp));
                landed = target;
                done.Add((photo, target));
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // Back to its own name rather than left with the temporary one.
                try
                {
                    MoveWithEverything(temp, photo.Path, index, photo.MovedTo(temp));
                }
                catch (Exception again) when (again is IOException or UnauthorizedAccessException)
                {
                    landed = temp;
                }
                failed.Add($"{photo.FileName}: {ex.Message}");
            }
            foreach (var (aside, final) in sidecars)
            {
                var to = final(landed);
                try
                {
                    File.Move(aside, File.Exists(to) ? aside[..aside.LastIndexOf(".sidecar", StringComparison.Ordinal)] + Path.GetExtension(to) : to);
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                }
            }
        }
        return done;
    }

    private static readonly string[] SidecarExtensions = [".xmp", ".aae"];

    /// <summary>
    /// The photo's sidecar files and where each goes for the photo's new path: IMG_1.jpg.xmp belongs to it alone,
    /// IMG_1.xmp and IMG_1.aae only when no other file is called IMG_1 (a RAW and JPEG pair shares them).
    /// </summary>
    private static List<(string Path, Func<string, string> Final)> Sidecars(string photo)
    {
        var found = new List<(string, Func<string, string>)>();
        var folder = Path.GetDirectoryName(photo)!;
        var stem = Path.GetFileNameWithoutExtension(photo);
        var pair = Directory.EnumerateFiles(folder, stem + ".*")
            .Any(f => !string.Equals(f, photo, StringComparison.OrdinalIgnoreCase) &&
                      Path.GetFileNameWithoutExtension(f).Equals(stem, StringComparison.OrdinalIgnoreCase) &&
                      !SidecarExtensions.Any(s => f.EndsWith(s, StringComparison.OrdinalIgnoreCase)) &&
                      !f.Contains(".renaming-", StringComparison.Ordinal));
        foreach (var ext in SidecarExtensions)
        {
            var own = photo + ext;
            if (File.Exists(own))
                found.Add((own, newPath => newPath + ext));
            var shared = Path.Combine(folder, stem + ext);
            if (!pair && File.Exists(shared))
                found.Add((shared, newPath => Path.Combine(Path.GetDirectoryName(newPath)!, Path.GetFileNameWithoutExtension(newPath) + ext)));
        }
        return found;
    }

    private static void MoveWithEverything(string from, string to, PhotoIndex index, IndexedPhoto photo)
    {
        File.Move(from, to);
        EditHistory.Transfer(from, to, copy: false);
        UploadFile.Carry(from, to);
        index.Move(photo, to);
        Albums.AlbumFile.FollowCover(from, to);
    }

    public static bool IsIn(string path, string folder) =>
        string.Equals(Path.GetFullPath(Path.GetDirectoryName(path)!), Path.GetFullPath(folder).TrimEnd(Path.DirectorySeparatorChar),
                      StringComparison.OrdinalIgnoreCase);

    private static string FreeName(string folder, string name)
    {
        var target = Path.Combine(folder, name);
        var stem = Path.GetFileNameWithoutExtension(name);
        var ext = Path.GetExtension(name);
        for (var n = 2; File.Exists(target); n++)
            target = Path.Combine(folder, $"{stem} ({n}){ext}");
        return target;
    }
}
