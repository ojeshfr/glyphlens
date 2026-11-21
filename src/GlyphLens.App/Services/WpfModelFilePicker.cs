using GlyphLens.App.ViewModels;
using Microsoft.Win32;

namespace GlyphLens.App.Services;

/// <summary>Shows the standard Windows file dialog to choose a translation model package.</summary>
public sealed class WpfModelFilePicker : IModelFilePicker
{
    public string? PickModelFile()
    {
        var dialog = new OpenFileDialog
        {
            Title = "Import a translation model",
            Filter = "Argos Translate models (*.argosmodel)|*.argosmodel|All files (*.*)|*.*",
            CheckFileExists = true,
            CheckPathExists = true,
            Multiselect = false,
            DereferenceLinks = true,
        };

        return dialog.ShowDialog() == true ? dialog.FileName : null;
    }
}
