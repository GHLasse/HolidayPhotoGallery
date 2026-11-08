using System.Buffers.Binary;
using System.Text;
using PhotoGallery.Core.Editing;
using PhotoGallery.Core.Metadata;

namespace PhotoGallery.Core.Tests;

public sealed class ColourProfileTests
{
    // Display P3 as Apple's profile stores it: D50-adapted primaries and the sRGB tone curve.
    internal static byte[] DisplayP3 => Icc("Display P3",
        [0.515102, 0.241182, -0.001049], [0.291965, 0.692236, 0.041882], [0.157153, 0.066582, 0.784378]);

    private static byte[] Srgb => Icc("sRGB IEC61966-2.1",
        [0.4360747, 0.2225045, 0.0139322], [0.3850649, 0.7168786, 0.0971045], [0.1430804, 0.0606169, 0.7141733]);

    /// <summary>A small ICC v2 profile of the kind phones embed: three primaries, a "para" sRGB curve per channel, a name.</summary>
    internal static byte[] Icc(string name, double[] red, double[] green, double[] blue)
    {
        static byte[] Xyz(double[] v)
        {
            var tag = new byte[20];
            Encoding.ASCII.GetBytes("XYZ ").CopyTo(tag, 0);
            for (var i = 0; i < 3; i++)
                BinaryPrimitives.WriteInt32BigEndian(tag.AsSpan(8 + i * 4), (int)Math.Round(v[i] * 65536));
            return tag;
        }
        var curve = new byte[32];
        Encoding.ASCII.GetBytes("para").CopyTo(curve, 0);
        BinaryPrimitives.WriteUInt16BigEndian(curve.AsSpan(8), 3);
        double[] p = [2.4, 1 / 1.055, 0.055 / 1.055, 1 / 12.92, 0.04045];
        for (var i = 0; i < 5; i++)
            BinaryPrimitives.WriteInt32BigEndian(curve.AsSpan(12 + i * 4), (int)Math.Round(p[i] * 65536));
        var text = Encoding.ASCII.GetBytes(name + "\0");
        var desc = new byte[12 + text.Length + 79 + 12];
        Encoding.ASCII.GetBytes("desc").CopyTo(desc, 0);
        BinaryPrimitives.WriteUInt32BigEndian(desc.AsSpan(8), (uint)text.Length);
        text.CopyTo(desc, 12);
        var white = Xyz([0.9642, 1.0, 0.8249]);

        var tags = new (string Sig, byte[] Data)[]
        {
            ("desc", desc), ("wtpt", white), ("rXYZ", Xyz(red)), ("gXYZ", Xyz(green)), ("bXYZ", Xyz(blue)),
            ("rTRC", curve), ("gTRC", curve), ("bTRC", curve),
        };
        var data = new List<byte>(new byte[128 + 4 + tags.Length * 12]);
        var table = new byte[4 + tags.Length * 12];
        BinaryPrimitives.WriteUInt32BigEndian(table, (uint)tags.Length);
        for (var i = 0; i < tags.Length; i++)
        {
            while (data.Count % 4 != 0)
                data.Add(0);
            Encoding.ASCII.GetBytes(tags[i].Sig).CopyTo(table, 4 + i * 12);
            BinaryPrimitives.WriteUInt32BigEndian(table.AsSpan(8 + i * 12), (uint)data.Count);
            BinaryPrimitives.WriteUInt32BigEndian(table.AsSpan(12 + i * 12), (uint)tags[i].Data.Length);
            data.AddRange(tags[i].Data);
        }
        var icc = data.ToArray();
        table.CopyTo(icc, 128);
        BinaryPrimitives.WriteUInt32BigEndian(icc, (uint)icc.Length);
        Encoding.ASCII.GetBytes("lcms").CopyTo(icc, 4);
        icc[8] = 2; icc[9] = 0x10;
        Encoding.ASCII.GetBytes("mntrRGB XYZ ").CopyTo(icc, 12);
        Encoding.ASCII.GetBytes("acsp").CopyTo(icc, 36);
        return icc;
    }

    [Fact]
    public void Reads_a_phone_profile_and_tells_P3_from_sRGB()
    {
        var p3 = ColourProfile.Parse(DisplayP3);
        var srgb = ColourProfile.Parse(Srgb);

        Assert.NotNull(p3);
        Assert.Equal("Display P3", p3.Description);
        Assert.False(p3.IsSrgb);
        Assert.NotNull(srgb);
        Assert.True(srgb.IsSrgb);
        Assert.Null(ColourProfile.Parse([1, 2, 3]));
        Assert.Null(ColourProfile.Parse(null));
    }

    [Fact]
    public void P3_colours_are_shown_as_the_same_colours_in_sRGB()
    {
        var p3 = ColourProfile.Parse(DisplayP3)!;
        // BGRA: grey, a P3 orange, and P3's purest red.
        byte[] pixels = [128, 128, 128, 255, 50, 100, 200, 255, 0, 0, 255, 255];

        p3.ToSrgb(pixels, 3, 1);

        // Grey stays grey (both share the white point and the tone curve).
        Assert.InRange(pixels[0], 127, 129);
        Assert.InRange(pixels[1], 127, 129);
        Assert.InRange(pixels[2], 127, 129);
        // The same colour needs bigger numbers in the smaller sRGB space: the orange gets more saturated.
        Assert.True(pixels[6] > 200, $"red {pixels[6]}");
        Assert.True(pixels[4] < 50, $"blue {pixels[4]}");
        // P3's red lies outside sRGB: the nearest sRGB red.
        Assert.Equal(255, pixels[10]);
        Assert.True(pixels[9] < 10 && pixels[8] < 10);
        Assert.Equal(255, pixels[11]); // alpha untouched
    }

    [Fact]
    public void Tells_the_colour_space_for_the_badge()
    {
        Assert.Equal("P3", PhotoQuality.ColourSpace("Display P3", "Uncalibrated", null, null));
        Assert.Equal("Adobe RGB", PhotoQuality.ColourSpace("Adobe RGB (1998)", null, null, null));
        Assert.Equal("sRGB", PhotoQuality.ColourSpace("sRGB IEC61966-2.1", null, null, null));
        Assert.Null(PhotoQuality.ColourSpace("ProPhoto RGB", "sRGB", null, null));
        Assert.Equal("sRGB", PhotoQuality.ColourSpace(null, "sRGB", "R98 - DCF basic file (sRGB)", null));
        Assert.Equal("Adobe RGB", PhotoQuality.ColourSpace(null, "Uncalibrated", "R03 - DCF option file (Adobe RGB)", null));
        Assert.Equal("sRGB", PhotoQuality.ColourSpace(null, null, null, "BT.709"));
        Assert.Null(PhotoQuality.ColourSpace(null, null, null, null));
    }

    [Fact]
    public void Lightroom_edits_count_only_when_Lightroom_says_they_are_edits()
    {
        Assert.True(PhotoQuality.Colour("""[{"XMP-crs:HasSettings":true,"XMP-crs:Exposure2012":"+1.00"}]""").OtherAppEdits);
        Assert.False(PhotoQuality.Colour("""[{"XMP-crs:HasSettings":false,"XMP-crs:Exposure2012":"0.00"}]""").OtherAppEdits);
        Assert.True(PhotoQuality.Colour("""[{"XMP-crs:CropTop":0.1}]""").OtherAppEdits);
        Assert.False(PhotoQuality.Colour("""[{"XMP-crs:RawFileName":"IMG_1.CR2"}]""").OtherAppEdits);
        var colour = PhotoQuality.Colour("""[{"ICC_Profile:ProfileDescription":"Display P3","XMP-hdrgm:Version":"1.0"}]""");
        Assert.Equal("P3", colour.Space);
        Assert.True(colour.Hdr);
    }
}
