using System.Buffers.Binary;
using System.IO.Compression;
using System.Text;
using PhotoGallery.Core.Library;
using PhotoGallery.Core.Metadata;
using PhotoGallery.Core.Tracks;

namespace PhotoGallery.Core.Tests;

public sealed class TrackTests : IDisposable
{
    private readonly TempFolder _folder = new();

    public void Dispose() => _folder.Dispose();

    private static DateTime Utc(int hour, int minute = 0, int second = 0) => new(2025, 7, 1, hour, minute, second, DateTimeKind.Utc);

    private static GpsTrack Read(string text, string extension) =>
        TrackReader.Read(new MemoryStream(Encoding.UTF8.GetBytes(text)), "test", extension);

    [Fact]
    public void Reads_gpx_track_points_with_their_times()
    {
        var track = Read("""
            <?xml version="1.0"?>
            <gpx version="1.1" xmlns="http://www.topografix.com/GPX/1/1">
              <metadata><time>2025-07-01T00:00:00Z</time></metadata>
              <trk><trkseg>
                <trkpt lat="38.7223" lon="-9.1393"><ele>20</ele><time>2025-07-01T10:00:00Z</time></trkpt>
                <trkpt lat="38.7300" lon="-9.1500"><time>2025-07-01T10:10:00Z</time></trkpt>
                <trkpt lat="38.7400" lon="-9.1600"></trkpt>
              </trkseg></trk>
            </gpx>
            """, ".gpx");
        Assert.Equal(2, track.Points.Count);
        Assert.Equal(new TrackPoint(Utc(10), 38.7223, -9.1393), track.Points[0]);
        Assert.Equal(Utc(10, 10), track.EndUtc);
    }

    [Fact]
    public void Reads_kml_tracks_and_time_stamped_placemarks()
    {
        var kml = """
            <kml xmlns="http://www.opengis.net/kml/2.2" xmlns:gx="http://www.google.com/kml/ext/2.2"><Document>
              <Placemark><gx:Track>
                <when>2025-07-01T10:00:00Z</when><when>2025-07-01T10:05:00Z</when>
                <gx:coord>-9.1393 38.7223 20</gx:coord><gx:coord>-9.15 38.73 22</gx:coord>
              </gx:Track></Placemark>
              <Placemark><TimeStamp><when>2025-07-01T12:00:00Z</when></TimeStamp>
                <Point><coordinates>-8.61,41.15,0</coordinates></Point></Placemark>
              <Placemark><Point><coordinates>1,2,0</coordinates></Point></Placemark>
            </Document></kml>
            """;
        var track = Read(kml, ".kml");
        Assert.Equal([Utc(10), Utc(10, 5), Utc(12)], track.Points.Select(p => p.Utc));
        Assert.Equal(38.73, track.Points[1].Latitude, 6);
        Assert.Equal(-8.61, track.Points[2].Longitude, 6);

        // The same inside a KMZ.
        using var zipped = new MemoryStream();
        using (var zip = new ZipArchive(zipped, ZipArchiveMode.Create, leaveOpen: true))
        using (var entry = new StreamWriter(zip.CreateEntry("doc.kml").Open()))
            entry.Write(kml);
        zipped.Position = 0;
        Assert.Equal(3, TrackReader.Read(zipped, "zip", ".kmz").Points.Count);
    }

    [Fact]
    public void Reads_the_android_timeline_export()
    {
        var track = Read("""
            {"semanticSegments":[
              {"startTime":"2025-07-01T12:00:00.000+02:00","endTime":"2025-07-01T13:00:00.000+02:00",
               "timelinePath":[{"point":"38.7223°, -9.1393°","time":"2025-07-01T12:00:00.000+02:00"},
                               {"point":"38.7300°, -9.1500°","time":"2025-07-01T12:30:00.000+02:00"}]},
              {"startTime":"2025-07-01T13:00:00.000+02:00","endTime":"2025-07-01T14:00:00.000+02:00",
               "visit":{"topCandidate":{"placeLocation":{"latLng":"38.6916°, -9.2160°"}}}}
            ],
            "rawSignals":[{"position":{"LatLng":"38.70°, -9.20°","timestamp":"2025-07-01T14:30:00.000+02:00"}}]}
            """, ".json");
        Assert.Equal([Utc(10), Utc(10, 30), Utc(11), Utc(12), Utc(12, 30)], track.Points.Select(p => p.Utc));
        Assert.Equal(38.6916, track.Points[2].Latitude, 6);
    }

    [Fact]
    public void Reads_the_iphone_timeline_export()
    {
        var track = Read("""
            [{"startTime":"2025-07-01T12:00:00.000+02:00","endTime":"2025-07-01T13:00:00.000+02:00",
              "timelinePath":[{"point":"geo:38.7223,-9.1393","durationMinutesOffsetFromStartTime":"0"},
                              {"point":"geo:38.73,-9.15","durationMinutesOffsetFromStartTime":"20"}]},
             {"startTime":"2025-07-01T13:00:00.000+02:00","endTime":"2025-07-01T13:40:00.000+02:00",
              "activity":{"start":"geo:38.73,-9.15","end":"geo:38.74,-9.16"}}]
            """, ".json");
        Assert.Equal([Utc(10), Utc(10, 20), Utc(11), Utc(11, 40)], track.Points.Select(p => p.Utc));
    }

    [Fact]
    public void Reads_old_takeout_files()
    {
        var records = Read("""
            {"locations":[{"latitudeE7":387223000,"longitudeE7":-91393000,"timestamp":"2025-07-01T10:00:00Z"},
                          {"latitudeE7":387300000,"longitudeE7":-91500000,"timestampMs":"1751364600000"}]}
            """, ".json");
        Assert.Equal([Utc(10), Utc(10, 10)], records.Points.Select(p => p.Utc));
        Assert.Equal(-9.1393, records.Points[0].Longitude, 6);

        var semantic = Read("""
            {"timelineObjects":[{"placeVisit":{"location":{"latitudeE7":387223000,"longitudeE7":-91393000},
              "duration":{"startTimestamp":"2025-07-01T10:00:00Z","endTimestamp":"2025-07-01T11:00:00Z"}}}]}
            """, ".json");
        Assert.Equal([Utc(10), Utc(11)], semantic.Points.Select(p => p.Utc));

        Assert.Throws<InvalidDataException>(() => Read("""{"hello":1}""", ".json"));
    }

    [Fact]
    public void Reads_garmin_fit_records()
    {
        var epoch = new DateTime(1989, 12, 31, 0, 0, 0, DateTimeKind.Utc);
        var start = (uint)(Utc(10) - epoch).TotalSeconds;
        static int Semi(double degrees) => (int)Math.Round(degrees * 2147483648.0 / 180);

        var body = new List<byte>();
        // Definition for local message 0 = record (20): timestamp, position_lat, position_long.
        body.AddRange([0x40, 0, 0, 20, 0, 3, 253, 4, 0x86, 0, 4, 0x85, 1, 4, 0x85]);
        void Record(uint ts, double lat, double lon)
        {
            body.Add(0x00);
            body.AddRange(BitConverter.GetBytes(ts));
            body.AddRange(BitConverter.GetBytes(Semi(lat)));
            body.AddRange(BitConverter.GetBytes(Semi(lon)));
        }
        Record(start, 38.7223, -9.1393);
        Record(start + 60, 38.73, -9.15);
        // A record whose time is a compressed step: definition without the timestamp, then a compressed header.
        body.AddRange([0x41, 0, 0, 20, 0, 2, 0, 4, 0x85, 1, 4, 0x85]);
        body.Add((byte)(0x80 | (1 << 5) | ((start + 70) & 0x1F)));
        body.AddRange(BitConverter.GetBytes(Semi(38.74)));
        body.AddRange(BitConverter.GetBytes(Semi(-9.16)));

        var header = new byte[14];
        header[0] = 14;
        header[1] = 0x20;
        BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(4), (uint)body.Count);
        "FIT"u8.CopyTo(header.AsSpan(9));
        header[8] = (byte)'.';
        var file = header.Concat(body).Concat(new byte[2]).ToArray();

        var track = TrackReader.Read(new MemoryStream(file), "ride", ".fit");
        Assert.Equal([Utc(10), Utc(10, 1), Utc(10, 1, 10)], track.Points.Select(p => p.Utc));
        Assert.Equal(38.7223, track.Points[0].Latitude, 5);
        Assert.Equal(-9.16, track.Points[2].Longitude, 5);
    }

    [Fact]
    public void Positions_between_points_are_interpolated_and_gaps_stay_blank()
    {
        var track = new GpsTrack("t", [new TrackPoint(Utc(10), 10, 20), new TrackPoint(Utc(10, 10), 11, 22), new TrackPoint(Utc(12), 12, 24)]);
        var gap = TimeSpan.FromMinutes(30);
        Assert.Equal(new GeoPosition(10.5, 21), track.PositionAt(Utc(10, 5), gap));
        Assert.Equal(new GeoPosition(11, 22), track.PositionAt(Utc(10, 10), gap));
        Assert.Null(track.PositionAt(Utc(11), gap)); // 1 h 50 min between the points: a gap
        Assert.Equal(new GeoPosition(10, 20), track.PositionAt(Utc(9, 50), gap)); // just before the start
        Assert.Null(track.PositionAt(Utc(9), gap));
        Assert.Equal(2, track.Between(Utc(10), Utc(11)).Count);
    }

    [Fact]
    public void Photos_are_matched_by_time_in_their_time_zone()
    {
        var track = new GpsTrack("Lisbon", [new TrackPoint(Utc(10), 38, -9), new TrackPoint(Utc(10, 20), 38.2, -9.2)]);
        IndexedPhoto Photo(string name, DateTime local, TimeSpan? offset, GeoPosition? location = null) =>
            new() { Path = name, Folder = "/", DateTaken = new DateTaken(local, offset), Location = location };
        var photos = new[]
        {
            Photo("offset.jpg", new DateTime(2025, 7, 1, 12, 10, 0), TimeSpan.FromHours(2)), // 10:10 UTC
            Photo("zone.jpg", new DateTime(2025, 7, 1, 11, 5, 0), null),                      // Lisbon summer time: 10:05 UTC
            Photo("placed.jpg", new DateTime(2025, 7, 1, 11, 5, 0), null, new GeoPosition(1, 1)),
            Photo("late.jpg", new DateTime(2025, 7, 1, 18, 0, 0), null),
        };
        var lisbon = TimeZoneInfo.CreateCustomTimeZone("Lisbon", TimeSpan.Zero, "Lisbon", "WET", "WEST",
            [TimeZoneInfo.AdjustmentRule.CreateAdjustmentRule(DateTime.MinValue.Date, DateTime.MaxValue.Date, TimeSpan.FromHours(1),
                TimeZoneInfo.TransitionTime.CreateFloatingDateRule(new DateTime(1, 1, 1, 1, 0, 0), 3, 5, DayOfWeek.Sunday),
                TimeZoneInfo.TransitionTime.CreateFloatingDateRule(new DateTime(1, 1, 1, 2, 0, 0), 10, 5, DayOfWeek.Sunday))]);

        var matches = TrackMatcher.Match(photos, [track], new TrackMatchOptions { CameraTimeZone = lisbon });
        Assert.Equal(["offset.jpg", "zone.jpg"], matches.Select(m => m.Path));
        Assert.Equal(38.1, matches[0].Position.Latitude, 6);
        Assert.Equal(38.05, matches[1].Position.Latitude, 6);

        // A camera clock 5 minutes fast, and replacing positions already there.
        var fast = TrackMatcher.Match(photos, [track], new TrackMatchOptions { CameraTimeZone = lisbon, CameraClockError = TimeSpan.FromMinutes(5), ReplaceExisting = true });
        Assert.Equal(["offset.jpg", "zone.jpg", "placed.jpg"], fast.Select(m => m.Path));
        Assert.Equal(38.0, fast[1].Position.Latitude, 6);

        // Without a time zone only photos that carry their offset are matched.
        Assert.Equal(["offset.jpg"], TrackMatcher.Match(photos, [track], new TrackMatchOptions()).Select(m => m.Path));
    }

    [Fact]
    public void The_track_library_adds_renames_and_deletes_tracks()
    {
        var library = new TrackLibrary(Path.Combine(_folder.Path, "Tracks"));
        Assert.Empty(library.List());
        var source = Path.Combine(_folder.Path, "walk.gpx");
        File.WriteAllText(source, """<gpx><trk><trkseg><trkpt lat="1" lon="2"><time>2025-07-01T10:00:00Z</time></trkpt></trkseg></trk></gpx>""");

        var first = library.Add(source);
        var second = library.Add(source);
        Assert.Equal(["walk", "walk (2)"], library.List().Select(t => t.Name).Order());
        Assert.Equal(1, library.List()[0].PointCount);

        var renamed = library.Rename(second, "Sintra / day 2");
        Assert.Equal("Sintra _ day 2.gpx", Path.GetFileName(renamed));
        library.Delete(first);
        Assert.Equal(["Sintra _ day 2"], library.List().Select(t => t.Name));
        Assert.Single(library.Tracks());

        var notATrack = Path.Combine(_folder.Path, "notes.txt");
        File.WriteAllText(notATrack, "hello");
        Assert.Throws<InvalidDataException>(() => library.Add(notATrack));
    }
}
