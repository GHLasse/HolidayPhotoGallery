using PhotoGallery.Core.Metadata;

namespace PhotoGallery.Core.Tests;

public class OrientationTests
{
    [Theory]
    [InlineData(1, 6)]
    [InlineData(6, 3)]
    [InlineData(3, 8)]
    [InlineData(8, 1)]
    [InlineData(2, 7)]
    [InlineData(0, 6)]
    public void Rotates_clockwise(int from, int expected) => Assert.Equal(expected, Orientation.RotateClockwise(from));

    [Fact]
    public void Four_turns_in_either_direction_return_to_start()
    {
        for (var o = 1; o <= 8; o++)
        {
            var cw = o;
            var ccw = o;
            for (var i = 0; i < 4; i++)
            {
                cw = Orientation.RotateClockwise(cw);
                ccw = Orientation.RotateCounterClockwise(ccw);
            }
            Assert.Equal(o, cw);
            Assert.Equal(o, ccw);
            Assert.Equal(o, Orientation.RotateCounterClockwise(Orientation.RotateClockwise(o)));
        }
    }

    [Theory]
    // A landscape-stored photo (4000 x 3000) that has to be turned upright (6 or 8) is portrait.
    [InlineData(256, 192, 4000, 3000, 6, true)]   // Windows still shows the thumbnail from before the turn
    [InlineData(192, 256, 4000, 3000, 6, false)]
    [InlineData(192, 256, 4000, 3000, 8, false)]
    [InlineData(256, 192, 4000, 3000, 1, false)]
    [InlineData(192, 256, 4000, 3000, 1, true)]   // the embedded preview was turned by another program, the photo not
    [InlineData(256, 192, 4000, 3000, 3, false)]  // upside down keeps the shape
    [InlineData(256, 250, 4000, 3000, 6, false)]  // about square previews aren't judged
    [InlineData(256, 192, 3000, 3000, 6, false)]  // nor square photos
    [InlineData(256, 192, null, null, 6, false)]  // nor photos of unknown size
    [InlineData(0, 0, 4000, 3000, 6, false)]
    public void Spots_a_preview_turned_the_wrong_way(int previewWidth, int previewHeight, int? width, int? height, int orientation, bool turned) =>
        Assert.Equal(turned, Orientation.PreviewLooksTurned(previewWidth, previewHeight, width, height, orientation));
}
