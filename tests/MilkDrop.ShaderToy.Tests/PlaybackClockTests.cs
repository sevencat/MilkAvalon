namespace MilkDrop.ShaderToy.Tests;

/// <summary>
/// <see cref="PlaybackClock"/> 的 <c>iFrame</c> 语义：网页端第一帧是 0，所以帧号在渲染完之后才加。
/// </summary>
public sealed class PlaybackClockTests
{
    [Fact]
    public void Frame_Starts_At_Zero_And_Only_Counts_Rendered_Frames()
    {
        var clock = new PlaybackClock();

        Assert.Equal(0, clock.Frame);

        // 推进时间不动帧号：这段时间里还没画过任何一帧
        clock.Advance(0.016);
        Assert.Equal(0, clock.Frame);

        clock.MarkRendered();
        Assert.Equal(1, clock.Frame);

        clock.Advance(0.016);
        clock.MarkRendered();
        Assert.Equal(2, clock.Frame);
    }

    [Fact]
    public void Reset_And_Seek_Put_Frame_Back_To_Zero()
    {
        var clock = new PlaybackClock();
        clock.Advance(0.016);
        clock.MarkRendered();

        clock.Reset();
        Assert.Equal(0, clock.Frame);

        clock.MarkRendered();
        clock.Seek(30.0);
        Assert.Equal(0, clock.Frame);
        Assert.Equal(30f, clock.Time);
    }
}
