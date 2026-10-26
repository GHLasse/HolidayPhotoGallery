using PhotoGallery.Core.ExifTool;
using PhotoGallery.Core.Metadata;

namespace PhotoGallery.Core.Tests;

public sealed class MetadataPasteTests : IAsyncLifetime
{
    private ExifToolProcess? _exifTool;
    private readonly TempFolder _temp = new();

    public Task InitializeAsync()
    {
        _exifTool = ExifToolProcess.TryStart();
        return Task.CompletedTask;
    }

    public async Task DisposeAsync()
    {
        if (_exifTool is not null)
            await _exifTool.DisposeAsync();
        _temp.Dispose();
    }

    private static DateTaken Date(string text) => DateTaken.TryParse(text, out var d) ? d : throw new FormatException(text);

    private static readonly PhotoMetadata Source = new()
    {
        Path = "source.jpg",
        Tags = ["Rome", "Holiday"],
        Rating = 4,
        Caption = "Colosseum",
        DateTaken = Date("2024-05-01 10:00:00+02:00"),
        Latitude = 41.89,
        Longitude = 12.49,
        Place = new PlaceName { Country = "Italy", City = "Rome" },
        Copyright = "© 2024 Lasse",
    };

    private static readonly CopiedMetadata Copied = new(Source, new Dictionary<string, string> { ["IFD0:Make"] = "Canon", ["IFD0:Model"] = "Canon EOS R6" });

    [Fact]
    public void Keeping_the_gaps_moves_the_earliest_photo_to_the_copied_date_and_the_others_with_it()
    {
        PhotoMetadata[] targets =
        [
            new() { Path = "b.jpg", DateTaken = Date("2010-01-01 12:30:00") },
            new() { Path = "a.jpg", DateTaken = Date("2010-01-01 12:00:00") },
            new() { Path = "undated.jpg" },
        ];

        var plan = MetadataPaste.Plan(Copied, targets, MetadataParts.Date, keepGaps: true).ToDictionary(p => p.Path, p => p.Changes);

        Assert.Equal(Date("2024-05-01 10:00:00+02:00"), plan["a.jpg"].DateTaken);
        Assert.Equal(Date("2024-05-01 10:30:00+02:00"), plan["b.jpg"].DateTaken);
        Assert.Equal(Date("2024-05-01 10:00:00+02:00"), plan["undated.jpg"].DateTaken);
        Assert.Null(plan["undated.jpg"].ShiftCreateDateBy);
    }

    [Fact]
    public void Without_keeping_gaps_every_photo_gets_exactly_the_copied_date()
    {
        PhotoMetadata[] targets = [new() { Path = "a.jpg", DateTaken = Date("2010-01-01 12:00:00") }, new() { Path = "b.jpg", DateTaken = Date("2011-01-01 12:00:00") }];

        var plan = MetadataPaste.Plan(Copied, targets, MetadataParts.Date, keepGaps: false);

        Assert.All(plan, p => Assert.Equal(Source.DateTaken, p.Changes.DateTaken));
    }

    [Fact]
    public void Only_ticked_parts_are_pasted_tags_are_added_and_the_source_itself_is_skipped()
    {
        PhotoMetadata[] targets = [Source, new() { Path = "a.jpg", Tags = ["Family"], Rating = 2 }];

        var plan = MetadataPaste.Plan(Copied, targets, MetadataParts.Tags | MetadataParts.Location | MetadataParts.Copyright, keepGaps: true);

        var (path, changes) = Assert.Single(plan);
        Assert.Equal("a.jpg", path);
        Assert.Equal(["Family", "Rome", "Holiday"], changes.Tags);
        Assert.Equal(new GeoPosition(41.89, 12.49), changes.Location);
        Assert.Equal("Rome", changes.Place?.City);
        Assert.Equal("© 2024 Lasse", changes.Copyright);
        Assert.Null(changes.Rating);
        Assert.Null(changes.Caption);
        Assert.Null(changes.DateTaken);
        Assert.Null(changes.Camera);
    }

    [Fact]
    public void Parts_the_copied_photo_has_nothing_for_are_not_available()
    {
        var bare = new CopiedMetadata(new PhotoMetadata { Path = "x.jpg", Tags = ["A"] }, new Dictionary<string, string>());
        Assert.Equal(MetadataParts.Tags, bare.Available);
        Assert.Equal(MetadataParts.All, Copied.Available);
    }

    [Fact]
    public void Undo_puts_back_what_the_photo_had()
    {
        var old = new PhotoMetadata { Path = "a.jpg", Tags = ["Family"], DateTaken = Date("2010-01-01 12:00:00"), Copyright = null };
        var oldCamera = new Dictionary<string, string> { ["IFD0:Make"] = "Nikon" };
        var changes = MetadataPaste.Plan(Copied, [old], MetadataParts.All, keepGaps: true)[0].Changes;

        var undo = MetadataPaste.Undo(old, oldCamera, changes);

        Assert.Equal(["Family"], undo.Tags);
        Assert.Equal(old.DateTaken, undo.DateTaken);
        Assert.Equal(-changes.ShiftCreateDateBy, undo.ShiftCreateDateBy);
        Assert.True(undo.RemoveLocation);
        Assert.True(undo.Place?.IsEmpty);
        Assert.Equal("Nikon", undo.Camera!["IFD0:Make"]);
        Assert.Equal("", undo.Camera["IFD0:Model"]);
        Assert.Equal("", undo.Copyright);
        Assert.Equal(0, undo.Rating);
        Assert.Equal("", undo.Caption);
    }

    [ExifToolFact]
    public async Task Camera_and_copyright_are_copied_into_another_photo_and_undone()
    {
        var service = new PhotoMetadataService(_exifTool!);
        var from = _temp.CopySample(".jpg", "from");
        var to = _temp.CopySample(".jpg", "to");
        await service.WriteAsync(from, new MetadataChanges
        {
            Camera = new Dictionary<string, string> { ["IFD0:Make"] = "Canon", ["IFD0:Model"] = "Canon EOS R6", ["ExifIFD:FNumber"] = "2.8", ["ExifIFD:ExposureTime"] = "0.004" },
            Copyright = "© 2024 Lasse",
            Creator = "Lasse",
        });
        await service.WriteAsync(to, new MetadataChanges { Camera = new Dictionary<string, string> { ["IFD0:Make"] = "", ["IFD0:Model"] = "", ["ExifIFD:FNumber"] = "", ["ExifIFD:ExposureTime"] = "" } });

        var copied = new CopiedMetadata(await service.ReadAsync(from), await service.ReadCameraAsync(from));
        var old = await service.ReadAsync(to);
        var oldCamera = await service.ReadCameraAsync(to);
        var (_, changes) = Assert.Single(MetadataPaste.Plan(copied, [old], MetadataParts.Camera | MetadataParts.Copyright, keepGaps: true));
        await service.WriteAsync(to, changes);

        var pasted = await service.ReadAsync(to);
        var camera = await service.ReadCameraAsync(to);
        Assert.Equal("Canon EOS R6", pasted.Camera);
        Assert.Equal("2.8", camera["ExifIFD:FNumber"]);
        Assert.Equal("0.004", camera["ExifIFD:ExposureTime"]);
        Assert.Equal("© 2024 Lasse", pasted.Copyright);
        Assert.Equal("Lasse", pasted.Creator);

        await service.WriteAsync(to, MetadataPaste.Undo(old, oldCamera, changes));
        var undone = await service.ReadAsync(to);
        Assert.Null(undone.Camera);
        Assert.Null(undone.Copyright);
        Assert.Empty(await service.ReadCameraAsync(to));
    }
}
