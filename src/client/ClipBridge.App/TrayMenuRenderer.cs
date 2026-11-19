using System.Drawing;
using Forms = System.Windows.Forms;

namespace ClipBridge.App;

public static class TrayMenuCheckLayout
{
    // WinForms 在非 100% 缩放下用 96 DPI 的列宽布局勾选框，勾选图会收到负的左坐标，
    // 左边缘被菜单边界裁掉。把负坐标夹回 0 即可让完整的勾选框可见。
    public static Rectangle ClampIntoMenu(Rectangle checkBounds) =>
        checkBounds.X < 0 ? new Rectangle(0, checkBounds.Y, checkBounds.Width, checkBounds.Height) : checkBounds;
}

internal sealed class TrayMenuRenderer : Forms.ToolStripProfessionalRenderer
{
    protected override void OnRenderItemCheck(Forms.ToolStripItemImageRenderEventArgs e)
    {
        var clamped = TrayMenuCheckLayout.ClampIntoMenu(e.ImageRectangle);
        base.OnRenderItemCheck(clamped == e.ImageRectangle
            ? e
            : new Forms.ToolStripItemImageRenderEventArgs(e.Graphics, e.Item, e.Image, clamped));
    }
}
