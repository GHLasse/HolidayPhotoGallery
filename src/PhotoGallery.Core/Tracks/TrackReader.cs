using System.Buffers.Binary;
using System.Globalization;
using System.IO.Compression;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Xml;

namespace PhotoGallery.Core.Tracks;

/// <summary>
/// Reads recorded tracks (G-4): GPX, KML and KMZ, Google Maps Timeline as exported from the phone (Settings ›
/// Location › Timeline › Export) and the older Google Takeout files (Records.json, the monthly Semantic Location
/// History), and Garmin FIT. Only points with a time count: a track without times can't place a photo.
/// </summary>
public static partial class TrackReader
{
    /// <summary>The file types the track library takes.</summary>
    public static readonly IReadOnlyList<string> Extensions = [".gpx", ".kml", ".kmz", ".json", ".fit"];

    public static bool IsTrackFile(string path) => Extensions.Contains(Path.GetExtension(path).ToLowerInvariant());

    /// <summary>Reads a track file; the track is named after the file. Throws <see cref="InvalidDataException"/> for a file it can't read.</summary>
    public static GpsTrack Read(string path)
    {
        using var stream = File.OpenRead(path);
        return Read(stream, Path.GetFileNameWithoutExtension(path), Path.GetExtension(path));
    }

    public static GpsTrack Read(Stream stream, string name, string extension)
    {
        try
        {
            var points = extension.ToLowerInvariant() switch
            {
                ".gpx" => ReadGpx(stream),
                ".kml" => ReadKml(stream),
                ".kmz" => ReadKmz(stream),
                ".json" => ReadJson(stream),
                ".fit" => ReadFit(stream),
                _ => throw new InvalidDataException($"{extension} is not a track file."),
            };
            return new GpsTrack(name, points);
        }
        catch (Exception ex) when (ex is XmlException or JsonException or FormatException or EndOfStreamException or ArgumentException)
        {
            throw new InvalidDataException($"The track could not be read: {ex.Message}", ex);
        }
    }

    // ---- GPX: <trkpt lat="…" lon="…"><time>…</time></trkpt>, also route points and waypoints with a time ----

    private static List<TrackPoint> ReadGpx(Stream stream)
    {
        var points = new List<TrackPoint>();
        using var reader = XmlReader.Create(stream, new XmlReaderSettings { DtdProcessing = DtdProcessing.Ignore, IgnoreWhitespace = true });
        double? lat = null, lon = null;
        while (reader.Read())
        {
            if (reader.NodeType != XmlNodeType.Element)
                continue;
            if (reader.LocalName is "trkpt" or "rtept" or "wpt")
            {
                lat = Number(reader.GetAttribute("lat"));
                lon = Number(reader.GetAttribute("lon"));
            }
            else if (reader.LocalName == "time" && lat is { } la && lon is { } lo)
            {
                // Moves on to the next node, which is the point's end (nothing a point needs).
                if (Time(reader.ReadElementContentAsString()) is { } utc)
                    points.Add(new TrackPoint(utc, la, lo));
                lat = lon = null;
            }
        }
        return points;
    }

    // ---- KML: <gx:Track> with <when> and <gx:coord> lists, or placemarks with a time stamp and a point ----

    private static List<TrackPoint> ReadKml(Stream stream)
    {
        var points = new List<TrackPoint>();
        using var reader = XmlReader.Create(stream, new XmlReaderSettings { DtdProcessing = DtdProcessing.Ignore, IgnoreWhitespace = true });
        var whens = new List<DateTime?>();
        var coords = new List<(double Lat, double Lon)>();
        var inTrack = false;
        DateTime? stamp = null;
        // Reading an element's text moves on to the next node already, so the loop only reads when nothing was.
        var more = reader.Read();
        while (more)
        {
            if (reader.NodeType == XmlNodeType.EndElement)
            {
                if (reader.LocalName == "Track")
                {
                    for (var i = 0; i < Math.Min(whens.Count, coords.Count); i++)
                        if (whens[i] is { } utc)
                            points.Add(new TrackPoint(utc, coords[i].Lat, coords[i].Lon));
                    whens.Clear();
                    coords.Clear();
                    inTrack = false;
                }
                else if (reader.LocalName == "Placemark")
                    stamp = null;
            }
            else if (reader.NodeType == XmlNodeType.Element)
            {
                switch (reader.LocalName)
                {
                    case "Track" when !reader.IsEmptyElement:
                        inTrack = true;
                        break;
                    case "when":
                        var when = Time(reader.ReadElementContentAsString());
                        // Inside <Track> it is one of the list; inside <TimeStamp> it dates the placemark.
                        if (inTrack)
                            whens.Add(when);
                        else
                            stamp = when;
                        more = !reader.EOF;
                        continue;
                    case "coord":
                        var parts = reader.ReadElementContentAsString().Split(' ', StringSplitOptions.RemoveEmptyEntries);
                        coords.Add(parts.Length >= 2 ? (Number(parts[1]) ?? double.NaN, Number(parts[0]) ?? double.NaN) : (double.NaN, double.NaN));
                        more = !reader.EOF;
                        continue;
                    case "coordinates" when stamp is { } at:
                        var first = reader.ReadElementContentAsString().Split((char[])[' ', '\n', '\r', '\t'], StringSplitOptions.RemoveEmptyEntries).FirstOrDefault()?.Split(',');
                        if (first is { Length: >= 2 } && Number(first[1]) is { } la && Number(first[0]) is { } lo)
                            points.Add(new TrackPoint(at, la, lo));
                        more = !reader.EOF;
                        continue;
                }
            }
            more = reader.Read();
        }
        return points;
    }

    private static List<TrackPoint> ReadKmz(Stream stream)
    {
        using var zip = new ZipArchive(stream, ZipArchiveMode.Read);
        var kml = zip.Entries.FirstOrDefault(e => e.FullName.Equals("doc.kml", StringComparison.OrdinalIgnoreCase))
                  ?? zip.Entries.FirstOrDefault(e => e.FullName.EndsWith(".kml", StringComparison.OrdinalIgnoreCase))
                  ?? throw new InvalidDataException("The KMZ file holds no KML.");
        using var inner = kml.Open();
        return ReadKml(inner);
    }

    // ---- Google Maps Timeline and Takeout ----

    private static List<TrackPoint> ReadJson(Stream stream)
    {
        using var doc = JsonDocument.Parse(stream, new JsonDocumentOptions { AllowTrailingCommas = true, MaxDepth = 256 });
        var points = new List<TrackPoint>();
        var root = doc.RootElement;
        if (root.ValueKind == JsonValueKind.Array)
        {
            // The iPhone's Timeline export: a list of segments.
            foreach (var segment in root.EnumerateArray())
                ReadSegment(segment, points);
            return points;
        }
        if (root.TryGetProperty("semanticSegments", out var segments))
            foreach (var segment in segments.EnumerateArray())
                ReadSegment(segment, points);
        if (root.TryGetProperty("rawSignals", out var signals))
            foreach (var signal in signals.EnumerateArray())
                if (signal.TryGetProperty("position", out var position) &&
                    LatLng(Prop(position, "LatLng") ?? Prop(position, "latLng")) is { } p && Time(Prop(position, "timestamp")) is { } t)
                    points.Add(new TrackPoint(t, p.Lat, p.Lon));
        if (root.TryGetProperty("locations", out var locations))
        {
            // Takeout's Records.json.
            foreach (var l in locations.EnumerateArray())
                if (E7(l, "latitudeE7") is { } lat && E7(l, "longitudeE7") is { } lon &&
                    (Time(Prop(l, "timestamp")) ?? MillisTime(Prop(l, "timestampMs"))) is { } t)
                    points.Add(new TrackPoint(t, lat, lon));
        }
        if (root.TryGetProperty("timelineObjects", out var objects))
        {
            // Takeout's Semantic Location History, one file a month.
            foreach (var o in objects.EnumerateArray())
            {
                if (o.TryGetProperty("placeVisit", out var visit) && visit.TryGetProperty("location", out var loc) &&
                    E7(loc, "latitudeE7") is { } vlat && E7(loc, "longitudeE7") is { } vlon && Duration(visit) is var (vs, ve))
                {
                    AddIf(points, vs, vlat, vlon);
                    AddIf(points, ve, vlat, vlon);
                }
                if (o.TryGetProperty("activitySegment", out var activity) && Duration(activity) is var (s, e))
                {
                    if (activity.TryGetProperty("startLocation", out var start) && E7(start, "latitudeE7") is { } slat && E7(start, "longitudeE7") is { } slon)
                        AddIf(points, s, slat, slon);
                    if (activity.TryGetProperty("endLocation", out var end) && E7(end, "latitudeE7") is { } elat && E7(end, "longitudeE7") is { } elon)
                        AddIf(points, e, elat, elon);
                }
            }
        }
        if (points.Count == 0 && !root.TryGetProperty("semanticSegments", out _) && !root.TryGetProperty("locations", out _) &&
            !root.TryGetProperty("timelineObjects", out _) && !root.TryGetProperty("rawSignals", out _))
            throw new InvalidDataException("This JSON file is not a Google Maps Timeline or Takeout location file.");
        return points;

        static (DateTime?, DateTime?) Duration(JsonElement e) =>
            e.TryGetProperty("duration", out var d) ? (Time(Prop(d, "startTimestamp")) ?? MillisTime(Prop(d, "startTimestampMs")),
                                                       Time(Prop(d, "endTimestamp")) ?? MillisTime(Prop(d, "endTimestampMs"))) : (null, null);
    }

    // A Timeline segment: a path with times, a visit (stayed at one place from start to end), or an activity (from here to there).
    private static void ReadSegment(JsonElement segment, List<TrackPoint> points)
    {
        if (segment.ValueKind != JsonValueKind.Object)
            return;
        var start = Time(Prop(segment, "startTime"));
        var end = Time(Prop(segment, "endTime"));
        if (segment.TryGetProperty("timelinePath", out var path) && path.ValueKind == JsonValueKind.Array)
        {
            foreach (var p in path.EnumerateArray())
            {
                var at = Time(Prop(p, "time"));
                if (at is null && start is { } s && Number(Prop(p, "durationMinutesOffsetFromStartTime")) is { } minutes)
                    at = s.AddMinutes(minutes);
                if (LatLng(Prop(p, "point")) is { } ll && at is { } t)
                    points.Add(new TrackPoint(t, ll.Lat, ll.Lon));
            }
        }
        if (segment.TryGetProperty("visit", out var visit) && visit.TryGetProperty("topCandidate", out var top))
        {
            var place = top.TryGetProperty("placeLocation", out var pl)
                ? pl.ValueKind == JsonValueKind.String ? pl.GetString() : Prop(pl, "latLng")
                : null;
            if (LatLng(place) is { } ll)
            {
                AddIf(points, start, ll.Lat, ll.Lon);
                AddIf(points, end, ll.Lat, ll.Lon);
            }
        }
        if (segment.TryGetProperty("activity", out var activity))
        {
            if (activity.TryGetProperty("start", out var a) && LatLng(a.ValueKind == JsonValueKind.String ? a.GetString() : Prop(a, "latLng")) is { } sa)
                AddIf(points, start, sa.Lat, sa.Lon);
            if (activity.TryGetProperty("end", out var b) && LatLng(b.ValueKind == JsonValueKind.String ? b.GetString() : Prop(b, "latLng")) is { } eb)
                AddIf(points, end, eb.Lat, eb.Lon);
        }
    }

    private static void AddIf(List<TrackPoint> points, DateTime? at, double lat, double lon)
    {
        if (at is { } t)
            points.Add(new TrackPoint(t, lat, lon));
    }

    private static string? Prop(JsonElement e, string name) =>
        e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v)
            ? v.ValueKind switch { JsonValueKind.String => v.GetString(), JsonValueKind.Number => v.GetRawText(), _ => null }
            : null;

    private static double? E7(JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number && v.TryGetInt64(out var n) ? n / 1e7 : null;

    // "48.8584°, 2.2945°" (Android) or "geo:48.8584,2.2945" (iPhone).
    private static (double Lat, double Lon)? LatLng(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return null;
        var m = LatLngPattern().Match(text);
        return m.Success && Number(m.Groups[1].Value) is { } lat && Number(m.Groups[2].Value) is { } lon ? (lat, lon) : null;
    }

    [GeneratedRegex(@"(-?\d+(?:\.\d+)?)\s*°?\s*,\s*(-?\d+(?:\.\d+)?)")]
    private static partial Regex LatLngPattern();

    private static DateTime? MillisTime(string? text) =>
        long.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var ms) ? DateTimeOffset.FromUnixTimeMilliseconds(ms).UtcDateTime : null;

    // ---- Garmin FIT: the "record" messages' time and position ----

    private static readonly DateTime FitEpoch = new(1989, 12, 31, 0, 0, 0, DateTimeKind.Utc);
    private const int RecordMessage = 20;
    private const int TimestampField = 253;

    private sealed record FitField(byte Number, byte Size);

    private sealed record FitDefinition(int Global, bool BigEndian, FitField[] Fields, int DeveloperBytes);

    private static List<TrackPoint> ReadFit(Stream stream)
    {
        using var memory = new MemoryStream();
        stream.CopyTo(memory);
        var data = memory.ToArray();
        if (data.Length < 12 || data[8] != '.' || data[9] != 'F' || data[10] != 'I' || data[11] != 'T')
            throw new InvalidDataException("This is not a FIT file.");
        int headerSize = data[0];
        var end = Math.Min(data.Length, headerSize + (int)BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(4, 4)));
        var definitions = new FitDefinition?[16];
        var points = new List<TrackPoint>();
        uint lastTimestamp = 0;
        var at = headerSize;
        while (at < end)
        {
            var header = data[at++];
            int local;
            uint? compressedTime = null;
            if ((header & 0x80) != 0)
            {
                // Compressed time header: a data message whose time is a 5-bit step from the last one.
                local = (header >> 5) & 0x03;
                var offset = (uint)(header & 0x1F);
                var time = (lastTimestamp & ~0x1Fu) + offset;
                if (offset < (lastTimestamp & 0x1F))
                    time += 0x20;
                compressedTime = time;
            }
            else if ((header & 0x40) != 0)
            {
                local = header & 0x0F;
                var hasDeveloper = (header & 0x20) != 0;
                var bigEndian = data[at + 1] == 1;
                var global = bigEndian ? BinaryPrimitives.ReadUInt16BigEndian(data.AsSpan(at + 2, 2)) : BinaryPrimitives.ReadUInt16LittleEndian(data.AsSpan(at + 2, 2));
                int count = data[at + 4];
                at += 5;
                var fields = new FitField[count];
                for (var i = 0; i < count; i++, at += 3)
                    fields[i] = new FitField(data[at], data[at + 1]);
                var developerBytes = 0;
                if (hasDeveloper)
                {
                    int devCount = data[at++];
                    for (var i = 0; i < devCount; i++, at += 3)
                        developerBytes += data[at + 1];
                }
                definitions[local] = new FitDefinition(global, bigEndian, fields, developerBytes);
                continue;
            }
            else
                local = header & 0x0F;

            var definition = definitions[local] ?? throw new InvalidDataException("The FIT file uses a message it never defined.");
            int? lat = null, lon = null;
            uint? timestamp = compressedTime;
            foreach (var field in definition.Fields)
            {
                if (at + field.Size > data.Length)
                    throw new EndOfStreamException("The FIT file ends in the middle of a message.");
                var span = data.AsSpan(at, field.Size);
                if (field.Size == 4)
                {
                    var value = definition.BigEndian ? BinaryPrimitives.ReadUInt32BigEndian(span) : BinaryPrimitives.ReadUInt32LittleEndian(span);
                    if (field.Number == TimestampField)
                        timestamp = value;
                    else if (definition.Global == RecordMessage && field.Number == 0 && value != 0x7FFFFFFF)
                        lat = unchecked((int)value);
                    else if (definition.Global == RecordMessage && field.Number == 1 && value != 0x7FFFFFFF)
                        lon = unchecked((int)value);
                }
                at += field.Size;
            }
            at += definition.DeveloperBytes;
            if (timestamp is { } ts && ts != 0xFFFFFFFF)
                lastTimestamp = ts;
            if (definition.Global == RecordMessage && lat is { } la && lon is { } lo && timestamp is { } when)
                points.Add(new TrackPoint(FitEpoch.AddSeconds(when), Semicircles(la), Semicircles(lo)));
        }
        return points;

        static double Semicircles(int value) => value * (180.0 / 2147483648.0);
    }

    // ---- Shared ----

    private static double? Number(string? text) =>
        double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var value) ? value : null;

    private static DateTime? Time(string? text) =>
        !string.IsNullOrWhiteSpace(text) && DateTimeOffset.TryParse(text, CultureInfo.InvariantCulture,
            DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var value)
            ? value.UtcDateTime
            : null;
}
