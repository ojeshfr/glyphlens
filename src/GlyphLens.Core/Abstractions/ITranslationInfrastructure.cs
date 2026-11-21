using System.Text.Json;
using GlyphLens.Core.Models;

namespace GlyphLens.Core.Abstractions;

/// <summary>
/// What the local translation engine has installed, and the explicit operation to add a model.
/// </summary>
/// <remarks>
/// Models come from files the user provides. Nothing here downloads anything.
/// </remarks>
public interface IModelCatalog
{
    /// <summary>Raised after the installed set may have changed (a model was imported, or the worker restarted).</summary>
    event EventHandler? Changed;

    /// <summary>The installed packages, storage location and any initialisation problems.</summary>
    /// <exception cref="WorkerException">The worker is unavailable.</exception>
    Task<ModelCatalog> GetCatalogAsync(CancellationToken cancellationToken);

    /// <summary>Whether <paramref name="source"/> can be translated into <paramref name="target"/> now, and by which route.</summary>
    /// <exception cref="WorkerException">The worker is unavailable.</exception>
    Task<RouteStatus> GetRouteStatusAsync(string source, string target, CancellationToken cancellationToken);

    /// <summary>
    /// Installs a translation package from a local <c>.argosmodel</c> file the user chose. The file is validated before it is installed.
    /// </summary>
    /// <exception cref="TranslationException">The file is not a valid package, or the install failed.</exception>
    /// <exception cref="WorkerException">The worker is unavailable.</exception>
    Task<TranslationPackageInfo> ImportPackageAsync(string localFilePath, CancellationToken cancellationToken);
}

/// <summary>
/// Sends requests to the local Python worker and returns the response payload.
/// </summary>
/// <remarks>Implemented by the worker process manager. Payloads are private data: implementations never log them.</remarks>
public interface IWorkerRequestChannel
{
    /// <summary>Sends <paramref name="messageType"/> with <paramref name="payload"/> and returns the response's payload (null when empty).</summary>
    /// <exception cref="WorkerException">The worker is not running, timed out, failed, or answered with an error (see <see cref="WorkerException.ErrorCode"/>).</exception>
    Task<JsonElement?> RequestAsync(string messageType, object? payload, TimeSpan timeout, CancellationToken cancellationToken);
}
