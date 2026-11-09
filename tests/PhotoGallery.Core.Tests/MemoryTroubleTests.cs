using System.Runtime.InteropServices;

namespace PhotoGallery.Core.Tests;

public class MemoryTroubleTests
{
    [Fact]
    public void A_lack_of_memory_is_told_apart_from_an_unreadable_file()
    {
        Assert.True(MemoryTrouble.IsMemoryFailure(new OutOfMemoryException()));
        Assert.True(MemoryTrouble.IsMemoryFailure(new COMException("WIC", unchecked((int)0x8007000E))));
        Assert.True(MemoryTrouble.IsMemoryFailure(new COMException("page file full", unchecked((int)0x800705AF))));
        Assert.True(MemoryTrouble.IsMemoryFailure(new COMException("graphics card reset", unchecked((int)0x887A0005))));
        Assert.True(MemoryTrouble.IsMemoryFailure(new InvalidOperationException("wrapped", new OutOfMemoryException())));
        Assert.True(MemoryTrouble.IsMemoryFailure(new AggregateException(new IOException(), new OutOfMemoryException())));

        Assert.False(MemoryTrouble.IsMemoryFailure(new IOException("damaged")));
        Assert.False(MemoryTrouble.IsMemoryFailure(new COMException("no codec", unchecked((int)0x88982F50))));
        Assert.False(MemoryTrouble.IsMemoryFailure(null));
    }

    [Fact]
    public void A_stuck_call_passes_too() =>
        Assert.True(MemoryTrouble.IsPassing(new TimeoutException()));

    [Fact]
    public void Tries_again_after_a_growing_wait_of_at_most_a_minute()
    {
        Assert.Equal(TimeSpan.FromSeconds(2), MemoryTrouble.RetryDelay(1));
        Assert.Equal(TimeSpan.FromSeconds(4), MemoryTrouble.RetryDelay(2));
        Assert.Equal(TimeSpan.FromSeconds(32), MemoryTrouble.RetryDelay(5));
        Assert.Equal(TimeSpan.FromSeconds(60), MemoryTrouble.RetryDelay(6));
        Assert.Equal(TimeSpan.FromSeconds(60), MemoryTrouble.RetryDelay(500));
    }

    [Fact]
    public void Only_a_nearly_full_memory_holds_work_up()
    {
        Assert.True(MemoryTrouble.IsLow(95));
        Assert.False(MemoryTrouble.IsLow(80));
        Assert.False(MemoryTrouble.IsLow(-1)); // Windows didn't say
    }
}
