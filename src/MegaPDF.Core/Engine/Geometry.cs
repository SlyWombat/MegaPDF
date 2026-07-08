namespace MegaPDF.Core.Engine;

/// <summary>
/// A point in page space, in PDF points (1/72 inch), origin at the top-left of the page.
/// The engine adapter is responsible for converting to/from PDF's native bottom-left origin.
/// </summary>
public readonly record struct PdfPoint(double X, double Y);

/// <summary>A rectangle in page space (top-left origin, PDF points).</summary>
public readonly record struct PdfRect(double X, double Y, double Width, double Height)
{
    public double Right => X + Width;
    public double Bottom => Y + Height;

    public bool Contains(PdfPoint p) =>
        p.X >= X && p.X <= Right && p.Y >= Y && p.Y <= Bottom;

    public PdfPoint Center => new(X + Width / 2, Y + Height / 2);
}
