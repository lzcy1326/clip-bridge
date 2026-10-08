using ClipBridge.Core.Services;

namespace ClipBridge.Tests;

public sealed class QuickPasteSelectionTests
{
    [Fact]
    public void SetItemCount_SelectsFirstItemAndClearsSelectionForEmptyList()
    {
        var selection = new QuickPasteSelection();

        selection.SetItemCount(3);
        Assert.Equal(0, selection.SelectedIndex);

        selection.SetItemCount(0);
        Assert.Null(selection.SelectedIndex);
    }

    [Fact]
    public void Move_WrapsSelectionAtBothEnds()
    {
        var selection = new QuickPasteSelection();
        selection.SetItemCount(3);

        Assert.Equal(2, selection.Move(-1));
        Assert.Equal(0, selection.Move(1));
        Assert.Equal(1, selection.Move(1));
    }

    [Fact]
    public void Move_ReturnsNullWhenThereAreNoItems()
    {
        var selection = new QuickPasteSelection();

        Assert.Null(selection.Move(1));
    }
}
