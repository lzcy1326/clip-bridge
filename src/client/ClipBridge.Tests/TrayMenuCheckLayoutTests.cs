using System.Drawing;
using ClipBridge.App;

namespace ClipBridge.Tests;

public sealed class TrayMenuCheckLayoutTests
{
    [Fact]
    public void NegativeLeftEdgeIsClampedToMenuEdge()
    {
        var clamped = TrayMenuCheckLayout.ClampIntoMenu(new Rectangle(-3, 4, 32, 32));

        Assert.Equal(new Rectangle(0, 4, 32, 32), clamped);
    }

    [Fact]
    public void VisibleCheckMarkKeepsItsPosition()
    {
        var bounds = new Rectangle(6, 4, 16, 16);

        Assert.Equal(bounds, TrayMenuCheckLayout.ClampIntoMenu(bounds));
    }
}
