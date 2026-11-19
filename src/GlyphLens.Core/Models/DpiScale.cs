using System.Globalization;

namespace GlyphLens.Core.Models;

/// <summary>
/// A display scale factor, relative to the Windows reference resolution of 96 DPI.
/// </summary>
/// <remarks>
/// Captured frames and <see cref="ScreenRectangle"/> values are in physical pixels. WPF layout uses
/// device-independent units (1/96 inch). The two are NOT interchangeable on displays scaled above
/// 100 %: use this type to convert explicitly, with the scale of the display the value belongs to.
/// </remarks>
public readonly record struct DpiScale
{
    /// <summary>The DPI value that corresponds to a scale factor of 1.0 (100 %).</summary>
    public const double ReferenceDpi = 96.0;

    /// <summary>100 % scaling.</summary>
    public static readonly DpiScale Identity = new(1.0);

    public DpiScale(double factor)
    {
        if (double.IsNaN(factor) || double.IsInfinity(factor) || factor < 0.25 || factor > 16.0)
        {
            throw new ArgumentOutOfRangeException(nameof(factor), factor, "Scale factor must be between 0.25 and 16.");
        }

        Factor = factor;
    }

    /// <summary>Scale factor, where 1.0 is 100 %, 1.25 is 125 % and so on.</summary>
    public double Factor { get; }

    /// <summary>The scale as a percentage, rounded to the nearest whole percent.</summary>
    public int Percent => (int)Math.Round(Factor * 100.0, MidpointRounding.AwayFromZero);

    /// <summary>Creates a scale from an effective DPI value (for example 144 for 150 %).</summary>
    public static DpiScale FromDpi(double dpi)
    {
        if (double.IsNaN(dpi) || dpi <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(dpi), dpi, "DPI must be positive.");
        }

        return new DpiScale(dpi / ReferenceDpi);
    }

    /// <summary>Converts physical pixels to device-independent units.</summary>
    public double PhysicalToDip(double physicalPixels) => physicalPixels / Factor;

    /// <summary>Converts device-independent units to physical pixels.</summary>
    public double DipToPhysical(double dips) => dips * Factor;

    public override string ToString() => string.Create(CultureInfo.InvariantCulture, $"{Percent}%");
}
