using PhotoGallery.Core.Editing;
using PhotoGallery.Core.Faces;

namespace PhotoGallery.Core.Tests;

public sealed class BeautyTests
{
    private const int W = 400, H = 400;

    // A drawn face: noisy skin in an ellipse on a noisy green background, dark eyes, yellowish teeth and red lips.
    private static (byte[] Pixels, BeautyFace Face) Portrait()
    {
        var random = new Random(7);
        var pixels = new byte[W * H * 4];
        var box = new FaceBox(0.3, 0.25, 0.4, 0.5);
        var face = BeautyFace.FromBox(box);
        var p = face.Points.Select(q => (X: q.X * W, Y: q.Y * H)).ToArray();
        for (var y = 0; y < H; y++)
        {
            for (var x = 0; x < W; x++)
            {
                var noise = random.Next(-14, 15);
                (int R, int G, int B) c = (60 + noise, 140 + noise, 70 + noise);
                if (Math.Pow((x - 200) / 80.0, 2) + Math.Pow((y - 200) / 100.0, 2) <= 1)
                    c = (215 + noise, 165 + noise, 140 + noise);
                foreach (var eye in p[..2])
                    if (Math.Pow(x - eye.X, 2) + Math.Pow(y - eye.Y, 2) <= 36)
                        c = (40, 30, 30);
                var mouth = ((p[3].X + p[4].X) / 2, (p[3].Y + p[4].Y) / 2);
                if (Math.Abs(x - mouth.Item1) < 22 && Math.Abs(y - mouth.Item2) < 4)
                    c = (225, 210, 160); // teeth
                else if (Math.Abs(x - mouth.Item1) < 24 && Math.Abs(y - mouth.Item2) < 8)
                    c = (190, 60, 70);   // lips
                var o = (y * W + x) * 4;
                (pixels[o], pixels[o + 1], pixels[o + 2], pixels[o + 3]) = ((byte)c.B, (byte)c.G, (byte)c.R, 255);
            }
        }
        return (pixels, face);
    }

    private static double Spread(byte[] pixels, int x0, int y0, int size)
    {
        var values = new List<double>();
        for (var y = y0; y < y0 + size; y++)
            for (var x = x0; x < x0 + size; x++)
                values.Add(pixels[(y * W + x) * 4 + 1]);
        var mean = values.Average();
        return Math.Sqrt(values.Average(v => (v - mean) * (v - mean)));
    }

    private static (int R, int G, int B) At(byte[] pixels, double x, double y)
    {
        var o = ((int)y * W + (int)x) * 4;
        return (pixels[o + 2], pixels[o + 1], pixels[o]);
    }

    [Fact]
    public void Skin_gets_smoother_and_the_background_stays()
    {
        var (pixels, face) = Portrait();
        var before = (byte[])pixels.Clone();
        Beauty.Apply(pixels, W, H, [face], 100, BeautyParts.SmoothSkin);
        // A cheek.
        Assert.True(Spread(pixels, 160, 225, 12) < Spread(before, 160, 225, 12) * 0.6,
                    $"skin noise {Spread(before, 160, 225, 12):0.0} → {Spread(pixels, 160, 225, 12):0.0}");
        Assert.Equal(before.AsSpan(0, W * 4 * 40).ToArray(), pixels.AsSpan(0, W * 4 * 40).ToArray());
        // The eyes stay dark and crisp.
        var eye = face.Points[0];
        Assert.True(At(pixels, eye.X * W, eye.Y * H).R < 60);
    }

    [Fact]
    public void Teeth_get_whiter_and_lips_stay_red()
    {
        var (pixels, face) = Portrait();
        var mouth = ((face.Points[3].X + face.Points[4].X) / 2 * W, (face.Points[3].Y + face.Points[4].Y) / 2 * H);
        var tooth = At(pixels, mouth.Item1, mouth.Item2);
        var lip = At(pixels, mouth.Item1, mouth.Item2 + 6);
        Beauty.Apply(pixels, W, H, [face], 100, BeautyParts.WhitenTeeth);
        var whiter = At(pixels, mouth.Item1, mouth.Item2);
        Assert.True(whiter.R - whiter.B < (tooth.R - tooth.B) / 2, $"teeth {tooth} → {whiter}");
        var lipAfter = At(pixels, mouth.Item1, mouth.Item2 + 6);
        Assert.True(Math.Abs(lipAfter.B - lip.B) <= 3 && lipAfter.R > 180, $"lips {lip} → {lipAfter}");
    }

    [Fact]
    public void Eyes_get_brighter_but_the_pupil_stays_dark()
    {
        var pixels = new byte[W * H * 4];
        Array.Fill(pixels, (byte)150);
        var face = BeautyFace.FromBox(new FaceBox(0.3, 0.25, 0.4, 0.5));
        var eye = (X: face.Points[0].X * W, Y: face.Points[0].Y * H);
        var o = ((int)eye.Y * W + (int)eye.X) * 4;
        pixels[o] = pixels[o + 1] = pixels[o + 2] = 10;
        Beauty.Apply(pixels, W, H, [face], 100, BeautyParts.BrightenEyes);
        Assert.True(At(pixels, eye.X + 3, eye.Y).G > 160);
        Assert.True(pixels[o + 1] < 20);
    }

    [Fact]
    public void Nothing_changes_without_faces_strength_or_parts()
    {
        var (pixels, face) = Portrait();
        var before = (byte[])pixels.Clone();
        Beauty.Apply(pixels, W, H, [], 100, BeautyParts.All);
        Beauty.Apply(pixels, W, H, [face], 0, BeautyParts.All);
        Beauty.Apply(pixels, W, H, [face], 100, BeautyParts.None);
        Assert.Equal(before, pixels);
    }

    [Fact]
    public void A_face_known_only_by_its_box_gets_eyes_and_a_mouth_inside_it()
    {
        var box = new FaceBox(0.2, 0.1, 0.3, 0.4);
        var face = BeautyFace.FromBox(box);
        Assert.Equal(5, face.Points.Count);
        Assert.All(face.Points, p => Assert.True(p.X > box.X && p.X < box.Right && p.Y > box.Y && p.Y < box.Bottom));
        Assert.True(face.Points[0].X < face.Points[1].X && face.Points[0].Y < face.Points[3].Y);
    }
}
