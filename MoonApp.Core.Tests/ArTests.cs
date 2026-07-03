using MoonApp.Core;
using Xunit;

namespace MoonApp.Core.Tests;

public class ArTests
{
    [Fact]
    public void StraightAhead_IsCentered()
    {
        var (x, y, inView) = Ar.Project(135, 10, 135, 10, 60, 90, 1000, 2000);
        Assert.True(inView);
        Assert.Equal(500, x, 1);
        Assert.Equal(1000, y, 1);
    }

    [Fact]
    public void RightAndUp_GoesRightAndUp()
    {
        var (x, y, inView) = Ar.Project(150, 20, 135, 10, 60, 90, 1000, 2000);
        Assert.True(inView);
        Assert.True(x > 500, $"x {x}");   // az napravo
        Assert.True(y < 1000, $"y {y}");  // výš = menší y
    }

    [Fact]
    public void OutsideFov_NotInView()
    {
        var (_, _, inView) = Ar.Project(220, 10, 135, 10, 60, 90, 1000, 2000);
        Assert.False(inView);
    }
}
