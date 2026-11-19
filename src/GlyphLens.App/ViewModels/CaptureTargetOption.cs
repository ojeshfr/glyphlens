using System.Globalization;
using GlyphLens.Core.Models;

namespace GlyphLens.App.ViewModels;

/// <summary>
/// A <see cref="CaptureTarget"/> with the text the target selector shows for it.
/// </summary>
public sealed class CaptureTargetOption
{
    public CaptureTargetOption(CaptureTarget target)
    {
        ArgumentNullException.ThrowIfNull(target);
        Target = target;
        Label = Describe(target);
    }

    public CaptureTarget Target { get; }

    public string Label { get; }

    public override string ToString() => Label;

    internal static string Describe(CaptureTarget target)
    {
        var size = string.Create(CultureInfo.InvariantCulture, $"{target.Width}×{target.Height}");

        if (target.Kind == CaptureTargetKind.Display)
        {
            var parts = new List<string> { size };
            if (target.Dpi is { } dpi)
            {
                parts.Add(dpi.ToString());
            }

            if (target.IsPrimary)
            {
                parts.Add("primary");
            }

            return $"{target.DisplayName} ({string.Join(", ", parts)})";
        }

        var process = string.IsNullOrEmpty(target.ProcessName) ? "unknown process" : target.ProcessName;
        return $"{target.DisplayName} — {process} ({size})";
    }
}
