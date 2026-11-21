namespace GlyphLens.App.ViewModels;

/// <summary>Lets the user choose a translation model file. The UI implementation shows a file dialog.</summary>
public interface IModelFilePicker
{
    /// <summary>Returns the chosen file's full path, or <see langword="null"/> when the user cancelled.</summary>
    string? PickModelFile();
}
