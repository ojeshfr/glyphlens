namespace GlyphLens.Core.Abstractions;

/// <summary>What a Windows shortcut (<c>.lnk</c>) points at.</summary>
/// <param name="TargetPath">The program it starts.</param>
/// <param name="WorkingDirectory">The folder the program starts in.</param>
/// <param name="Description">The tooltip text.</param>
/// <param name="IconPath">The file that holds the icon (usually the program itself).</param>
/// <param name="IconIndex">The icon's index in <paramref name="IconPath"/>.</param>
public sealed record ShortcutDefinition(string TargetPath, string WorkingDirectory, string Description, string IconPath, int IconIndex = 0);

/// <summary>Creates and reads Windows shortcut files.</summary>
public interface IShortcutFiles
{
    /// <summary>Writes a shortcut at <paramref name="shortcutPath"/> (overwriting a file that is there).</summary>
    /// <exception cref="IOException">The shortcut could not be written.</exception>
    void Create(string shortcutPath, ShortcutDefinition definition);

    /// <summary>Reads the shortcut at <paramref name="shortcutPath"/>.</summary>
    /// <exception cref="IOException">The file is missing or is not a shortcut.</exception>
    ShortcutDefinition Read(string shortcutPath);
}
