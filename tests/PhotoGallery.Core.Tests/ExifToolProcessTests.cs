using PhotoGallery.Core.ExifTool;

namespace PhotoGallery.Core.Tests;

public class ExifToolProcessTests
{
    [Fact]
    public void Plain_arguments_are_passed_unchanged() =>
        Assert.Equal("-XMP-dc:Subject=Strand Münster", ExifToolProcess.EncodeArgument("-XMP-dc:Subject=Strand Münster"));

    [Fact]
    public void Line_breaks_and_backslashes_are_escaped() =>
        Assert.Equal(@"#[CSTR]-XMP-dc:Title=a\nb \\ c", ExifToolProcess.EncodeArgument("-XMP-dc:Title=a\nb \\ c"));
}

public class ExifToolRecoveryTests
{
    [Fact]
    public async Task A_dead_ExifTool_is_started_again_by_the_next_command()
    {
        await using var exifTool = ExifToolProcess.TryStart();
        if (exifTool is null)
            return; // ExifTool isn't installed here
        var troubles = new List<string>();
        exifTool.Trouble += troubles.Add;
        var version = (await exifTool.RunAsync(["-ver"])).Output.Trim();

        exifTool.KillForTest(); // as Perl does when another app has taken the memory

        Assert.Equal(version, (await exifTool.RunAsync(["-ver"])).Output.Trim());
        Assert.Equal(1, exifTool.Restarts);
        Assert.Single(troubles);
    }

    [Fact]
    public async Task An_ExifTool_that_stops_answering_is_given_up_and_replaced()
    {
        if (OperatingSystem.IsWindows())
            return; // the stand-in below is a shell script
        using var temp = new TempFolder();
        var hanging = Path.Combine(temp.Path, "exiftool");
        await File.WriteAllTextAsync(hanging, "#!/bin/sh\nexec sleep 600\n");
        File.SetUnixFileMode(hanging, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        await using var exifTool = new ExifToolProcess(hanging) { SilenceTimeout = TimeSpan.FromMilliseconds(500) };

        var watch = System.Diagnostics.Stopwatch.StartNew();
        await Assert.ThrowsAsync<ExifToolException>(() => exifTool.RunAsync(["-ver"]));
        // The next command isn't stuck behind the first: it starts a new process (which hangs too, here).
        await Assert.ThrowsAsync<ExifToolException>(() => exifTool.RunAsync(["-ver"]));

        Assert.True(watch.Elapsed < TimeSpan.FromSeconds(20), $"took {watch.Elapsed}");
        Assert.Equal(1, exifTool.Restarts);
    }
}
