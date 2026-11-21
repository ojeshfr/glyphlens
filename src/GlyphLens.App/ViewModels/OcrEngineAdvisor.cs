using System.ComponentModel;
using GlyphLens.Core.Abstractions;
using GlyphLens.Core.Events;
using GlyphLens.Core.Models;
using GlyphLens.Core.Ocr;

namespace GlyphLens.App.ViewModels;

/// <summary>
/// Keeps the text-recognition engine in step with the language situation: when Chinese is what is being translated and the local
/// Chinese OCR is ready, it is selected (unless the user picked an engine themselves); when it is not ready, the user is told what
/// is missing instead of Chinese text silently going unread.
/// </summary>
/// <remarks>
/// <para>
/// The decision itself is the pure <see cref="LocalOcrAdvisor"/>; this class only gathers its inputs (the chosen translation
/// source, the installed translation models, the worker's report about the local OCR, the OCR choice) and shows the outcome in the
/// OCR panel and among the translation notices (so it also reaches the overlay). It re-evaluates whenever one of those changes.
/// It never starts the worker: readiness is known once the worker runs (translation turned on), and until then the advice says so.
/// </para>
/// </remarks>
public sealed class OcrEngineAdvisor : IDisposable
{
    private readonly OcrViewModel _ocr;
    private readonly TranslationViewModel _translation;
    private readonly IWorkerProcessManager _worker;
    private readonly SynchronizationContext? _ui;
    private bool _evaluating;
    private bool _disposed;

    /// <param name="ocr">The recognition panel whose language entry is selected and which shows the explanation.</param>
    /// <param name="translation">Provides the chosen source language and the installed models, and shows the notice.</param>
    /// <param name="worker">Source of the worker's report about the local OCR.</param>
    /// <param name="uiContext">Where worker callbacks are delivered; <see langword="null"/> means inline.</param>
    public OcrEngineAdvisor(OcrViewModel ocr, TranslationViewModel translation, IWorkerProcessManager worker, SynchronizationContext? uiContext)
    {
        ArgumentNullException.ThrowIfNull(ocr);
        ArgumentNullException.ThrowIfNull(translation);
        ArgumentNullException.ThrowIfNull(worker);

        _ocr = ocr;
        _translation = translation;
        _worker = worker;
        _ui = uiContext;

        _translation.PropertyChanged += OnPropertyChanged;
        _ocr.PropertyChanged += OnPropertyChanged;
        _worker.StatusChanged += OnWorkerStatusChanged;

        Evaluate();
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _translation.PropertyChanged -= OnPropertyChanged;
        _ocr.PropertyChanged -= OnPropertyChanged;
        _worker.StatusChanged -= OnWorkerStatusChanged;
    }

    /// <summary>Recomputes the advice from the current state and applies it.</summary>
    public void Evaluate()
    {
        if (_disposed || _evaluating)
        {
            return;
        }

        _evaluating = true;
        try
        {
            var windowsLanguages = _ocr.Languages
                .Where(o => o.Tag is not null && !string.Equals(o.Tag, OcrEngineSelector.LocalEngineTag, StringComparison.Ordinal))
                .Select(o => new OcrLanguage(o.Tag!, o.Label))
                .ToList();

            bool chinese = LocalOcrAdvisor.IsChineseSituation(_translation.SelectedSource.Code, _translation.InstalledSources);
            var local = _worker.Status == WorkerStatus.Ready ? LocalOcrAdvisor.Classify(_worker.LastHealth) : LocalOcrState.Unknown;
            var advice = LocalOcrAdvisor.Advise(chinese, _ocr.SelectedLanguage.Tag, _ocr.UserChoseLanguage, local, windowsLanguages);

            if (advice.SwitchToLocal)
            {
                _ = _ocr.SelectLanguageAutomatically(OcrEngineSelector.LocalEngineTag);
            }

            _ocr.ShowEngineAdvice(advice);
            _translation.SetEngineNotice(advice.Message, advice.Severity == OcrAdviceSeverity.Problem, advice.Severity == OcrAdviceSeverity.Info);
        }
        finally
        {
            _evaluating = false;
        }
    }

    private void OnPropertyChanged(object? sender, PropertyChangedEventArgs args)
    {
        if (args.PropertyName is nameof(TranslationViewModel.SelectedSource) or nameof(TranslationViewModel.InstalledSources) or nameof(OcrViewModel.SelectedLanguage) or nameof(TranslationViewModel.IsEnabled))
        {
            Evaluate();
        }
    }

    private void OnWorkerStatusChanged(object? sender, WorkerStatusChangedEventArgs args)
    {
        if (_ui is null)
        {
            Evaluate();
        }
        else
        {
            _ui.Post(_ => Evaluate(), null);
        }
    }
}
