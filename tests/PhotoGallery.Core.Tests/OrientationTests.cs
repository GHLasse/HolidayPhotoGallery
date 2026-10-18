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
}
