using PhotoGallery.Core.ExifTool;

namespace PhotoGallery.Core.Tests;

/// <summary>A test that needs a real ExifTool; it is skipped when none is installed.</summary>
public sealed class ExifToolFactAttribute : FactAttribute
{
    public ExifToolFactAttribute()
    {
        if (ExifToolLocator.Find() is null)
            Skip = "ExifTool is not installed (run scripts/Get-ExifTool.ps1 or put exiftool on the PATH).";
    }
}

public sealed class ExifToolTheoryAttribute : TheoryAttribute
{
    public ExifToolTheoryAttribute()
    {
        if (ExifToolLocator.Find() is null)
            Skip = "ExifTool is not installed (run scripts/Get-ExifTool.ps1 or put exiftool on the PATH).";
    }
}

/// <summary>A temporary folder with copies of the sample images, deleted afterwards.</summary>
public sealed class TempFolder : IDisposable
{
    public string Path { get; } = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "PhotoGalleryTests", Guid.NewGuid().ToString("N"));

    public TempFolder() => Directory.CreateDirectory(Path);

    public string CopySample(string extension, string? name = null, string? subfolder = null)
    {
        var source = System.IO.Path.Combine(AppContext.BaseDirectory, "TestData", "sample" + extension);
        var dir = subfolder is null ? Path : System.IO.Path.Combine(Path, subfolder);
        Directory.CreateDirectory(dir);
        var target = System.IO.Path.Combine(dir, (name ?? "photo") + extension);
        File.Copy(source, target);
        return target;
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(Path, recursive: true);
        }
        catch (IOException)
        {
        }
    }
}
