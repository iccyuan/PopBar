using System.Windows;
using System.Windows.Media;

namespace PopBar.Plugins.Builtin.Screenshot;

/// <summary>
/// Overlay for the zoomed pixels of the capture magnifier, as in Snipaste: a hairline between every source pixel, a
/// translucent band along the cursor's row and column, and a black-and-white box around the cursor's own pixel.
/// Lines are one device pixel wide and snapped to the device grid, so they stay crisp at any scaling.
/// </summary>
internal sealed class MagnifierGrid : FrameworkElement
{
    private static readonly Color GridLine = Color.FromArgb(64, 128, 128, 128);
    private static readonly Color Band = Color.FromArgb(56, 59, 130, 246);

    public MagnifierGrid(int columns, int rows, double cell)
    {
        Columns = columns; Rows = rows; Cell = cell;
        Width = columns * cell; Height = rows * cell;
        IsHitTestVisible = false;
    }

    public int Columns { get; }
    public int Rows { get; }
    public double Cell { get; }

    private int _cursorColumn, _cursorRow;

    /// <summary>The cell under the cursor; not always the middle one, since the sample is clamped at the screen edge.</summary>
    public void SetCursor(int column, int row)
    {
        if (column == _cursorColumn && row == _cursorRow) return;
        _cursorColumn = column; _cursorRow = row;
        InvalidateVisual();
    }

    protected override void OnRender(DrawingContext dc)
    {
        double px = 1 / VisualTreeHelper.GetDpi(this).DpiScaleX; // one device pixel, in DIP
        double half = px / 2;
        var guides = new GuidelineSet();
        for (int i = 0; i <= Columns; i++) guides.GuidelinesX.Add(i * Cell + half);
        for (int j = 0; j <= Rows; j++) guides.GuidelinesY.Add(j * Cell + half);
        dc.PushGuidelineSet(guides);

        var band = new SolidColorBrush(Band);
        dc.DrawRectangle(band, null, new Rect(0, _cursorRow * Cell, Width, Cell));
        dc.DrawRectangle(band, null, new Rect(_cursorColumn * Cell, 0, Cell, _cursorRow * Cell));
        dc.DrawRectangle(band, null, new Rect(_cursorColumn * Cell, (_cursorRow + 1) * Cell, Cell, Height - (_cursorRow + 1) * Cell));

        var line = new Pen(new SolidColorBrush(GridLine), px);
        for (int i = 1; i < Columns; i++) dc.DrawLine(line, new Point(i * Cell, 0), new Point(i * Cell, Height));
        for (int j = 1; j < Rows; j++) dc.DrawLine(line, new Point(0, j * Cell), new Point(Width, j * Cell));

        // Cursor pixel: white inner and black outer ring, readable on any colour.
        var box = new Rect(_cursorColumn * Cell, _cursorRow * Cell, Cell, Cell);
        dc.DrawRectangle(null, new Pen(Brushes.Black, px), box);
        box.Inflate(-px, -px);
        dc.DrawRectangle(null, new Pen(Brushes.White, px), box);
        dc.Pop();
    }
}
