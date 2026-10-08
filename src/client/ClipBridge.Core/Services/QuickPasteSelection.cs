namespace ClipBridge.Core.Services;

public sealed class QuickPasteSelection
{
    private int _itemCount;
    private int _selectedIndex = -1;

    public int? SelectedIndex => _selectedIndex >= 0 ? _selectedIndex : null;

    public void SetItemCount(int itemCount)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(itemCount);
        _itemCount = itemCount;
        _selectedIndex = itemCount == 0 ? -1 : 0;
    }

    public void Select(int index)
    {
        if (index >= 0 && index < _itemCount)
        {
            _selectedIndex = index;
        }
    }

    public int? Move(int offset)
    {
        if (_itemCount == 0)
        {
            return null;
        }

        _selectedIndex = ((_selectedIndex + offset) % _itemCount + _itemCount) % _itemCount;
        return _selectedIndex;
    }
}
