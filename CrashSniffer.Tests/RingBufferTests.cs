using CrashSniffer.Services;
using Xunit;

namespace CrashSniffer.Tests;

public class RingBufferTests
{
    [Fact]
    public void Add_BelowCapacity_ContainsAll()
    {
        var buf = new RingBuffer<int>(5);
        for (int i = 0; i < 3; i++) buf.Add(i);
        Assert.Equal(new[] { 0, 1, 2 }, buf.ToArray());
    }

    [Fact]
    public void Add_ExceedsCapacity_DropsOldest()
    {
        var buf = new RingBuffer<int>(3);
        for (int i = 0; i < 5; i++) buf.Add(i);
        Assert.Equal(new[] { 2, 3, 4 }, buf.ToArray());
    }

    [Fact]
    public void Count_ReflectsActualCount()
    {
        var buf = new RingBuffer<int>(10);
        Assert.Equal(0, buf.Count);
        buf.Add(1); buf.Add(2);
        Assert.Equal(2, buf.Count);
    }

    [Fact]
    public void Clear_ResetsBuffer()
    {
        var buf = new RingBuffer<int>(5);
        buf.Add(1); buf.Add(2);
        buf.Clear();
        Assert.Equal(0, buf.Count);
        Assert.Empty(buf.ToArray());
    }

    [Fact]
    public void ToArray_ReturnsChronologicalOrder()
    {
        var buf = new RingBuffer<int>(4);
        for (int i = 0; i < 10; i++) buf.Add(i);
        var arr = buf.ToArray();
        Assert.Equal(new[] { 6, 7, 8, 9 }, arr);
    }
}
