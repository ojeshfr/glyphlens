using System.Text.Json;
using GlyphLens.Core.Abstractions;
using GlyphLens.Core.Models;
using GlyphLens.Infrastructure.IPC;

namespace GlyphLens.Infrastructure.Translation;

/// <summary>
/// Reads what the worker's translation engine has installed (<c>models.list</c>, <c>models.status</c>) and installs a
/// package the user chose (<c>models.install</c>). Nothing here downloads anything.
/// </summary>
/// <remarks>
/// <para>
/// Language codes are validated before anything is sent, and the worker's responses are parsed strictly: a malformed
/// response, or a route that claims to be available without a model version, is a
/// <see cref="WorkerErrorKind.ProtocolViolation"/>.
/// </para>
/// <para>
/// <see cref="ImportPackageAsync"/> checks the file locally first (a path, absolute, local, <c>.argosmodel</c>, existing) so
/// an obvious mistake is reported at once; the worker repeats and extends the validation (it opens the archive). A network
/// (UNC) or device path is refused so that choosing a package never starts a network connection. <see cref="Changed"/> is
/// raised after a successful import, and by <see cref="NotifyChanged"/> (for example when the worker restarted and may have
/// loaded a different set). It is not raised when an import fails.
/// </para>
/// </remarks>
public sealed class WorkerModelCatalog : IModelCatalog
{
    private const string PackageExtension = ".argosmodel";

    private readonly IWorkerRequestChannel _channel;
    private readonly TranslationClientOptions _options;

    public WorkerModelCatalog(IWorkerRequestChannel channel, TranslationClientOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(channel);

        _channel = channel;
        _options = options ?? new TranslationClientOptions();
    }

    /// <inheritdoc />
    public event EventHandler? Changed;

    /// <inheritdoc />
    public async Task<ModelCatalog> GetCatalogAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        var payload = await _channel
            .RequestAsync(WorkerMessageTypes.ModelsList, null, _options.ModelsTimeout, cancellationToken)
            .ConfigureAwait(false);
        return TranslationWire.ParseModelCatalog(payload);
    }

    /// <inheritdoc />
    /// <exception cref="ArgumentException">A language code is not valid.</exception>
    public async Task<RouteStatus> GetRouteStatusAsync(string source, string target, CancellationToken cancellationToken)
    {
        LanguageCode.Validate(source, nameof(source));
        LanguageCode.Validate(target, nameof(target));
        cancellationToken.ThrowIfCancellationRequested();

        var payload = await _channel
            .RequestAsync(WorkerMessageTypes.ModelsStatus, new TranslationWire.RouteStatusRequest(source, target), _options.ModelsTimeout, cancellationToken)
            .ConfigureAwait(false);
        return TranslationWire.ParseRouteStatus(payload);
    }

    /// <inheritdoc />
    public async Task<TranslationPackageInfo> ImportPackageAsync(string localFilePath, CancellationToken cancellationToken)
    {
        var fullPath = ValidateImportPath(localFilePath);
        cancellationToken.ThrowIfCancellationRequested();

        JsonElement? response;
        try
        {
            response = await _channel
                .RequestAsync(WorkerMessageTypes.ModelsInstall, new TranslationWire.InstallRequest(fullPath), _options.InstallTimeout, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (WorkerException ex) when (TranslationWire.TryMapInstallError(ex, out var mapped))
        {
            throw mapped;
        }

        var package = TranslationWire.ParseInstallResult(response);
        RaiseChanged();
        return package;
    }

    /// <summary>Raises <see cref="Changed"/>. Call it when the installed set may have changed without an import here (the worker restarted).</summary>
    public void NotifyChanged() => RaiseChanged();

    /// <summary>Checks the chosen file without touching the worker and returns its canonical full path.</summary>
    private static string ValidateImportPath(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            throw Fail("No package file was chosen.");
        }

        if (path.StartsWith(@"\\", StringComparison.Ordinal) || path.StartsWith("//", StringComparison.Ordinal))
        {
            throw Fail("Network and device paths are not supported. Copy the package to a local drive first.");
        }

        if (!Path.IsPathFullyQualified(path))
        {
            throw Fail("The package path must be an absolute path.");
        }

        if (!string.Equals(Path.GetExtension(path), PackageExtension, StringComparison.OrdinalIgnoreCase))
        {
            throw Fail("The package file must have the .argosmodel extension.");
        }

        string fullPath;
        try
        {
            fullPath = Path.GetFullPath(path);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or IOException)
        {
            throw Fail("The package path is not valid.", ex);
        }

        return File.Exists(fullPath) ? fullPath : throw Fail("The package file does not exist.");
    }

    private static TranslationException Fail(string message, Exception? inner = null) =>
        new(new TranslationError(TranslationErrorKind.Failed, message), inner);

    private void RaiseChanged()
    {
        var handler = Changed;
        if (handler is null)
        {
            return;
        }

        foreach (var subscriber in handler.GetInvocationList().Cast<EventHandler>())
        {
            try
            {
                subscriber(this, EventArgs.Empty);
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                // A faulty subscriber must not turn a successful import into a failure or stop the others.
            }
        }
    }
}
