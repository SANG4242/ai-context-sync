using System.Drawing;
using System.Windows.Forms;

namespace AIContextSyncTray;

internal sealed class ModernMenuColorTable : ProfessionalColorTable
{
    public override Color ToolStripDropDownBackground => Color.White;
    public override Color MenuBorder => Color.FromArgb(225, 227, 230);
    public override Color MenuItemSelected => Color.FromArgb(242, 244, 247);
    public override Color MenuItemSelectedGradientBegin => MenuItemSelected;
    public override Color MenuItemSelectedGradientEnd => MenuItemSelected;
    public override Color MenuItemBorder => Color.Transparent;
    public override Color CheckBackground => Color.FromArgb(235, 240, 248);
    public override Color CheckSelectedBackground => Color.FromArgb(220, 230, 245);
    public override Color CheckPressedBackground => Color.FromArgb(210, 225, 242);
    public override Color SeparatorDark => Color.FromArgb(235, 237, 240);
    public override Color SeparatorLight => Color.White;
}

internal sealed class ModernMenuRenderer : ToolStripProfessionalRenderer
{
    public ModernMenuRenderer() : base(new ModernMenuColorTable())
    {
        RoundedEdges = true;
    }

    protected override void OnRenderToolStripBorder(ToolStripRenderEventArgs e)
    {
        using var pen = new Pen(Color.FromArgb(220, 223, 228));
        e.Graphics.DrawRectangle(pen, new Rectangle(0, 0, e.ToolStrip.Width - 1, e.ToolStrip.Height - 1));
    }

    protected override void OnRenderSeparator(ToolStripSeparatorRenderEventArgs e)
    {
        using var pen = new Pen(Color.FromArgb(238, 240, 242));
        var y = e.Item.Height / 2;
        e.Graphics.DrawLine(pen, 10, y, e.Item.Width - 10, y);
    }
}
