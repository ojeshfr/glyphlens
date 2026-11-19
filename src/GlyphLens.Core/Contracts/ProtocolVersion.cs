using System.Diagnostics.CodeAnalysis;
using System.Globalization;

namespace GlyphLens.Core.Contracts;

/// <summary>
/// The version of the GlyphLens app/worker IPC protocol, in <c>MAJOR.MINOR</c> form.
/// </summary>
/// <remarks>
/// Peers are compatible when their major versions match. Minor versions add optional
/// fields or message types only. See docs/IPC-PROTOCOL.md.
/// </remarks>
public readonly record struct ProtocolVersion
{
    /// <summary>The protocol version implemented by this build.</summary>
    public static readonly ProtocolVersion Current = new(1, 0);

    public ProtocolVersion(int major, int minor)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(major);
        ArgumentOutOfRangeException.ThrowIfNegative(minor);

        Major = major;
        Minor = minor;
    }

    public int Major { get; }

    public int Minor { get; }

    /// <summary>Returns <see langword="true"/> when a peer speaking <paramref name="other"/> can talk to this version.</summary>
    public bool IsCompatibleWith(ProtocolVersion other) => Major == other.Major;

    public static ProtocolVersion Parse(string value) =>
        TryParse(value, out var version)
            ? version
            : throw new FormatException($"'{value}' is not a valid protocol version (expected MAJOR.MINOR).");

    public static bool TryParse([NotNullWhen(true)] string? value, out ProtocolVersion version)
    {
        version = default;

        if (string.IsNullOrEmpty(value))
        {
            return false;
        }

        var parts = value.Split('.');
        if (parts.Length != 2
            || !TryParseComponent(parts[0], out var major)
            || !TryParseComponent(parts[1], out var minor)
            || major == 0)
        {
            return false;
        }

        version = new ProtocolVersion(major, minor);
        return true;
    }

    public override string ToString() => string.Create(CultureInfo.InvariantCulture, $"{Major}.{Minor}");

    // Digits only: rejects signs, whitespace and leading zeros (other than "0" itself).
    private static bool TryParseComponent(string text, out int number)
    {
        number = 0;
        if (text.Length == 0 || (text.Length > 1 && text[0] == '0') || !text.All(char.IsAsciiDigit))
        {
            return false;
        }

        return int.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out number);
    }
}
