using System.Windows;
using System.Windows.Media;

namespace ImgViewer.Controls;

public partial class ImageViewer
{
    public double TestDipPerPx => _dipPerPx;
    public double TestOffsetX => MarginLeft;
    public double TestOffsetY => MarginTop;
    public double TestDisplayedWidth => DisplayedWidth;
    public double TestDisplayedHeight => DisplayedHeight;
    public double TestViewportWidth => ActualWidth;
    public double TestViewportHeight => ActualHeight;

    public Matrix TestMatrix => new(TestDipPerPx, 0, 0, TestDipPerPx, TestOffsetX, TestOffsetY);

    public void PanForTest(double dx, double dy) => Pan(dx, dy);
}