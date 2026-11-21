using GlyphLens.Core.Models;
using GlyphLens.Infrastructure.Translation;

namespace GlyphLens.Infrastructure.Tests.Translation;

public sealed class WorkerModelCatalogTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "glyphlens-catalog-tests-" + Guid.NewGuid().ToString("N"));

    public WorkerModelCatalogTests()
    {
        Directory.CreateDirectory(_directory);
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(_directory, recursive: true);
        }
        catch (IOException)
        {
            // Best effort: a leftover temp directory is harmless.
        }
    }

    private static readonly string[] DeEn = ["de", "en"];
    private static readonly string[] DeEnFr = ["de", "en", "fr"];

    private static WorkerModelCatalog Create(FakeWorkerRequestChannel channel) => new(channel);

    private static FakeWorkerRequestChannel ChannelAnswering(object? response)
    {
        var channel = new FakeWorkerRequestChannel();
        channel.Respond(_ => response);
        return channel;
    }

    private static object Package(string from = "de", string to = "en", string version = "1.9") =>
        new { from, to, fromName = "German", toName = "English", version, packageName = $"translate-{from}_{to}" };

    private static object CatalogJson(string state = "installed", object[]? packages = null, string[]? problems = null) =>
        new
        {
            engine = "argos-translate",
            engineVersion = "1.11.0",
            state,
            storagePath = @"C:\Users\me\AppData\Local\GlyphLens\models",
            packages = packages ?? [Package()],
            problems = problems ?? [],
        };

    private static object InstallJson(object? package = null, bool replaced = false) => new { package = package ?? Package(), replaced };

    private string PackageFile(string name = "translate-de_en-1_9.argosmodel")
    {
        var path = Path.Combine(_directory, name);
        File.WriteAllBytes(path, [1, 2, 3]);
        return path;
    }

    // ---- models.list ----

    [Fact]
    public async Task GetCatalogAsync_RequestsTheList_AndParsesTheCatalog()
    {
        var channel = ChannelAnswering(CatalogJson(packages: [Package("de", "en"), Package("fr", "en", "1.2")], problems: ["one"]));

        var catalog = await Create(channel).GetCatalogAsync(CancellationToken.None);

        var call = Assert.Single(channel.Calls);
        Assert.Equal("models.list", call.MessageType);
        Assert.Null(call.Payload);
        Assert.Equal(TimeSpan.FromSeconds(60), call.Timeout);

        Assert.Equal("argos-translate", catalog.Engine);
        Assert.Equal(@"C:\Users\me\AppData\Local\GlyphLens\models", catalog.StoragePath);
        Assert.Equal(2, catalog.Packages.Count);
        var german = catalog.Packages[0];
        Assert.Equal(new TranslationPackageInfo("de", "en", "German", "English", "1.9", "translate-de_en"), german);
        Assert.Equal("1.2", catalog.Packages[1].Version);
        Assert.Equal(["one"], catalog.Problems);
        Assert.Equal(["de", "fr"], catalog.InstalledSources);
        Assert.True(catalog.HasTarget("en"));
        Assert.False(catalog.HasTarget("fr"));
    }

    [Fact]
    public async Task GetCatalogAsync_ReportsAnEngineThatIsNotInstalled()
    {
        var channel = ChannelAnswering(new
        {
            engine = "argos-translate",
            state = "not_installed",
            storagePath = (string?)null,
            packages = Array.Empty<object>(),
            problems = Array.Empty<string>(),
        });

        var catalog = await Create(channel).GetCatalogAsync(CancellationToken.None);

        Assert.Empty(catalog.Packages);
        Assert.Null(catalog.StoragePath);
        Assert.Empty(catalog.Problems);
    }

    [Fact]
    public async Task GetCatalogAsync_ReportsAnUnavailableEngineWithItsProblems_EvenWithoutAPackageList()
    {
        var channel = ChannelAnswering("""{ "engine": "argos-translate", "state": "unavailable", "problems": [ "argostranslate is not installed" ] }""");

        var catalog = await Create(channel).GetCatalogAsync(CancellationToken.None);

        Assert.Empty(catalog.Packages);
        Assert.Equal(["argostranslate is not installed"], catalog.Problems);
    }

    [Fact]
    public async Task GetCatalogAsync_BoundsAndCleansTextThatComesFromThePackageFile()
    {
        var channel = ChannelAnswering(new
        {
            engine = "argos-translate",
            state = "installed",
            storagePath = "C:\\models",
            packages = new object[]
            {
                new { from = "de", to = "en", fromName = "Ger\r\nman" + new string('x', 500), toName = "English\u0007", version = "1.9", packageName = "translate-de_en" },
            },
            problems = new[] { "line one\nline two" + new string('y', 1000) },
        });

        var catalog = await Create(channel).GetCatalogAsync(CancellationToken.None);

        var package = Assert.Single(catalog.Packages);
        Assert.Equal(128, package.FromName.Length);
        Assert.StartsWith("Ger  man", package.FromName, StringComparison.Ordinal);
        Assert.Equal("English ", package.ToName);
        var problem = Assert.Single(catalog.Problems);
        Assert.Equal(300, problem.Length);
        Assert.StartsWith("line one line two", problem, StringComparison.Ordinal);
    }

    public static TheoryData<string?> MalformedCatalogs() => new()
    {
        { null },
        { "[]" },
        { "{}" },
        { """{ "state": "installed", "packages": [] }""" }, // no engine
        { """{ "engine": " ", "state": "installed", "packages": [] }""" },
        { """{ "engine": 5, "state": "installed", "packages": [] }""" },
        { """{ "engine": "argos-translate", "packages": [] }""" }, // no state
        { """{ "engine": "argos-translate", "state": "ready", "packages": [] }""" },
        { """{ "engine": "argos-translate", "state": "installed" }""" }, // no packages
        { """{ "engine": "argos-translate", "state": "not_installed" }""" },
        { """{ "engine": "argos-translate", "state": "installed", "packages": {} }""" },
        { """{ "engine": "argos-translate", "state": "installed", "packages": [ 1 ] }""" },
        { """{ "engine": "argos-translate", "state": "installed", "packages": [ { "from": "DE", "to": "en", "fromName": "a", "toName": "b", "version": "1", "packageName": "p" } ] }""" },
        { """{ "engine": "argos-translate", "state": "installed", "packages": [ { "from": "de", "to": "english", "fromName": "a", "toName": "b", "version": "1", "packageName": "p" } ] }""" },
        { """{ "engine": "argos-translate", "state": "installed", "packages": [ { "from": "de", "to": "en", "toName": "b", "version": "1", "packageName": "p" } ] }""" },
        { """{ "engine": "argos-translate", "state": "installed", "packages": [ { "from": "de", "to": "en", "fromName": "a", "toName": "b", "version": "", "packageName": "p" } ] }""" },
        { """{ "engine": "argos-translate", "state": "installed", "packages": [ { "from": "de", "to": "en", "fromName": "a", "toName": "b", "version": "1" } ] }""" },
        { """{ "engine": "argos-translate", "state": "installed", "packages": [ { "from": "de", "to": "en", "fromName": "a", "toName": "b", "version": 1, "packageName": "p" } ] }""" },
        { """{ "engine": "argos-translate", "state": "installed", "packages": [], "problems": {} }""" },
        { """{ "engine": "argos-translate", "state": "installed", "packages": [], "problems": [ 1 ] }""" },
        { """{ "engine": "argos-translate", "state": "installed", "packages": [], "storagePath": 5 }""" },
    };

    [Theory]
    [MemberData(nameof(MalformedCatalogs))]
    public async Task GetCatalogAsync_RejectsAMalformedResponse_AsAProtocolViolation(string? response)
    {
        var ex = await Assert.ThrowsAsync<WorkerException>(() => Create(ChannelAnswering(response)).GetCatalogAsync(CancellationToken.None));

        Assert.Equal(WorkerErrorKind.ProtocolViolation, ex.Kind);
    }

    [Theory]
    [InlineData(WorkerErrorKind.NotConnected, null)]
    [InlineData(WorkerErrorKind.RequestTimeout, null)]
    [InlineData(WorkerErrorKind.Rejected, "engine_unavailable")]
    public async Task GetCatalogAsync_LetsWorkerFailuresThrough(WorkerErrorKind kind, string? code)
    {
        var failure = new WorkerException(kind, "The worker failed.", code);
        var channel = new FakeWorkerRequestChannel();
        channel.Throw(failure);

        var ex = await Assert.ThrowsAsync<WorkerException>(() => Create(channel).GetCatalogAsync(CancellationToken.None));

        Assert.Same(failure, ex);
    }

    [Fact]
    public async Task GetCatalogAsync_WithACancelledToken_ThrowsWithoutAskingTheWorker()
    {
        var channel = ChannelAnswering(CatalogJson());
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Create(channel).GetCatalogAsync(cts.Token));

        Assert.Empty(channel.Calls);
    }

    // ---- models.status ----

    [Fact]
    public async Task GetRouteStatusAsync_AnAvailableRoute_IsParsed()
    {
        var channel = ChannelAnswering(new { source = "de", target = "en", available = true, route = DeEn, modelVersion = "argos:de-en@1.9", missingPackage = (string?)null, issue = "none" });

        var status = await Create(channel).GetRouteStatusAsync("de", "en", CancellationToken.None);

        var call = Assert.Single(channel.Calls);
        Assert.Equal("models.status", call.MessageType);
        Assert.Equal(["source", "target"], call.Json.EnumerateObject().Select(p => p.Name).Order());
        Assert.Equal("de", call.Json.GetProperty("source").GetString());
        Assert.Equal("en", call.Json.GetProperty("target").GetString());
        Assert.Equal(TimeSpan.FromSeconds(60), call.Timeout);

        Assert.Equal("de", status.Source);
        Assert.Equal("en", status.Target);
        Assert.True(status.Available);
        Assert.Equal(["de", "en"], status.Route);
        Assert.Equal("argos:de-en@1.9", status.ModelVersion);
        Assert.Null(status.MissingPackage);
        Assert.Equal(RouteIssue.None, status.Issue);
    }

    [Fact]
    public async Task GetRouteStatusAsync_APivotRoute_KeepsEveryHop()
    {
        var channel = ChannelAnswering(new { source = "de", target = "fr", available = true, route = DeEnFr, modelVersion = "argos:de-en@1.9+en-fr@1.9", issue = "none" });

        var status = await Create(channel).GetRouteStatusAsync("de", "fr", CancellationToken.None);

        Assert.Equal(["de", "en", "fr"], status.Route);
        Assert.Equal("argos:de-en@1.9+en-fr@1.9", status.ModelVersion);
    }

    [Theory]
    [InlineData("model_missing", RouteIssue.ModelMissing)]
    [InlineData("unsupported_pair", RouteIssue.UnsupportedPair)]
    [InlineData("engine_unavailable", RouteIssue.EngineUnavailable)]
    public async Task GetRouteStatusAsync_AnUnavailableRoute_CarriesItsIssue(string issue, RouteIssue expected)
    {
        var channel = ChannelAnswering(new { source = "ja", target = "en", available = false, route = Array.Empty<string>(), modelVersion = (string?)null, missingPackage = "translate-ja_en", issue });

        var status = await Create(channel).GetRouteStatusAsync("ja", "en", CancellationToken.None);

        Assert.False(status.Available);
        Assert.Equal(expected, status.Issue);
        Assert.Equal("translate-ja_en", status.MissingPackage);
        Assert.Null(status.ModelVersion);
        Assert.Empty(status.Route);
    }

    public static TheoryData<string?> MalformedRouteStatuses() => new()
    {
        { null },
        { "[]" },
        { "{}" },
        // available without a model version: it keys the translation cache, so the route cannot be trusted
        { """{ "source": "de", "target": "en", "available": true, "route": ["de","en"], "issue": "none" }""" },
        { """{ "source": "de", "target": "en", "available": true, "route": ["de","en"], "modelVersion": null, "issue": "none" }""" },
        { """{ "source": "de", "target": "en", "available": true, "route": ["de","en"], "modelVersion": "", "issue": "none" }""" },
        { """{ "source": "de", "target": "en", "available": true, "route": ["de","en"], "modelVersion": "   ", "issue": "none" }""" },
        { """{ "source": "de", "target": "en", "available": true, "route": ["de","en"], "modelVersion": 5, "issue": "none" }""" },
        { """{ "source": "de", "target": "en", "available": true, "modelVersion": "m@1", "issue": "model_missing" }""" }, // available, yet an issue
        { """{ "source": "de", "target": "en", "available": false, "issue": "none" }""" }, // unavailable without a reason
        { """{ "source": "de", "target": "en", "available": false, "issue": "bogus" }""" },
        { """{ "source": "de", "target": "en", "available": false }""" },
        { """{ "source": "de", "target": "en", "issue": "model_missing" }""" }, // no available
        { """{ "source": "de", "target": "en", "available": "true", "modelVersion": "m@1", "issue": "none" }""" },
        { """{ "source": "de", "target": "en", "available": 1, "modelVersion": "m@1", "issue": "none" }""" },
        { """{ "target": "en", "available": false, "issue": "model_missing" }""" },
        { """{ "source": "DE", "target": "en", "available": false, "issue": "model_missing" }""" },
        { """{ "source": "de", "target": "", "available": false, "issue": "model_missing" }""" },
        { """{ "source": "de", "target": "en", "available": true, "route": "de", "modelVersion": "m@1", "issue": "none" }""" },
        { """{ "source": "de", "target": "en", "available": true, "route": ["de", 5], "modelVersion": "m@1", "issue": "none" }""" },
        { """{ "source": "de", "target": "en", "available": true, "route": ["DE", "en"], "modelVersion": "m@1", "issue": "none" }""" },
        { """{ "source": "de", "target": "en", "available": false, "missingPackage": 5, "issue": "model_missing" }""" },
    };

    [Theory]
    [MemberData(nameof(MalformedRouteStatuses))]
    public async Task GetRouteStatusAsync_RejectsAMalformedResponse_AsAProtocolViolation(string? response)
    {
        var ex = await Assert.ThrowsAsync<WorkerException>(() => Create(ChannelAnswering(response)).GetRouteStatusAsync("de", "en", CancellationToken.None));

        Assert.Equal(WorkerErrorKind.ProtocolViolation, ex.Kind);
    }

    [Fact]
    public async Task GetRouteStatusAsync_DropsAMissingPackageThatDoesNotLookLikeAPackageName()
    {
        var channel = ChannelAnswering("""{ "source": "ja", "target": "en", "available": false, "missingPackage": "x; rm -rf /", "issue": "model_missing" }""");

        var status = await Create(channel).GetRouteStatusAsync("ja", "en", CancellationToken.None);

        Assert.Equal(RouteIssue.ModelMissing, status.Issue);
        Assert.Null(status.MissingPackage);
    }

    [Theory]
    [InlineData("", "en")]
    [InlineData("de", "")]
    [InlineData("DE", "en")]
    [InlineData("de", "english")]
    [InlineData("d", "en")]
    [InlineData(null, "en")]
    [InlineData("de", null)]
    public async Task GetRouteStatusAsync_ValidatesTheLanguageCodesLocally(string? source, string? target)
    {
        var channel = ChannelAnswering(new { });

        await Assert.ThrowsAnyAsync<ArgumentException>(() => Create(channel).GetRouteStatusAsync(source!, target!, CancellationToken.None));

        Assert.Empty(channel.Calls);
    }

    [Fact]
    public async Task GetRouteStatusAsync_LetsWorkerFailuresThrough()
    {
        var failure = new WorkerException(WorkerErrorKind.NotConnected, "The worker is not running.");
        var channel = new FakeWorkerRequestChannel();
        channel.Throw(failure);

        var ex = await Assert.ThrowsAsync<WorkerException>(() => Create(channel).GetRouteStatusAsync("de", "en", CancellationToken.None));

        Assert.Same(failure, ex);
    }

    // ---- models.install: the local checks ----

    public static TheoryData<string?> UnusablePaths() => new()
    {
        { null },
        { string.Empty },
        { "   " },
        { @"translate-de_en.argosmodel" }, // relative
        { @"models\translate-de_en.argosmodel" },
        { @"..\translate-de_en.argosmodel" },
        { @"C:translate-de_en.argosmodel" }, // relative to the current directory of drive C
        { @"\translate-de_en.argosmodel" }, // relative to the current drive
        { @"\\server\share\translate-de_en.argosmodel" }, // network path
        { "//server/share/translate-de_en.argosmodel" },
        { @"\\?\C:\translate-de_en.argosmodel" }, // device path
    };

    [Theory]
    [MemberData(nameof(UnusablePaths))]
    public async Task ImportPackageAsync_RefusesAnUnusablePathLocally_WithoutAskingTheWorker(string? path)
    {
        var channel = ChannelAnswering(InstallJson());
        var catalog = Create(channel);
        var changed = 0;
        catalog.Changed += (_, _) => changed++;

        var ex = await Assert.ThrowsAsync<TranslationException>(() => catalog.ImportPackageAsync(path!, CancellationToken.None));

        Assert.Equal(TranslationErrorKind.Failed, ex.Error.Kind);
        Assert.False(string.IsNullOrWhiteSpace(ex.Message));
        Assert.Empty(channel.Calls);
        Assert.Equal(0, changed);
    }

    [Fact]
    public async Task ImportPackageAsync_RefusesAFileWithoutTheArgosmodelExtension()
    {
        var path = PackageFile("translate-de_en-1_9.zip");
        var channel = ChannelAnswering(InstallJson());

        var ex = await Assert.ThrowsAsync<TranslationException>(() => Create(channel).ImportPackageAsync(path, CancellationToken.None));

        Assert.Contains(".argosmodel", ex.Message, StringComparison.Ordinal);
        Assert.DoesNotContain(path, ex.Message, StringComparison.Ordinal);
        Assert.Empty(channel.Calls);
    }

    [Fact]
    public async Task ImportPackageAsync_RefusesAFileThatDoesNotExist()
    {
        var path = Path.Combine(_directory, "missing.argosmodel");
        var channel = ChannelAnswering(InstallJson());

        var ex = await Assert.ThrowsAsync<TranslationException>(() => Create(channel).ImportPackageAsync(path, CancellationToken.None));

        Assert.Equal(TranslationErrorKind.Failed, ex.Error.Kind);
        Assert.Contains("does not exist", ex.Message, StringComparison.Ordinal);
        Assert.Empty(channel.Calls);
    }

    [Fact]
    public async Task ImportPackageAsync_RefusesADirectoryNamedLikeAPackage()
    {
        var path = Path.Combine(_directory, "folder.argosmodel");
        Directory.CreateDirectory(path);
        var channel = ChannelAnswering(InstallJson());

        await Assert.ThrowsAsync<TranslationException>(() => Create(channel).ImportPackageAsync(path, CancellationToken.None));

        Assert.Empty(channel.Calls);
    }

    [Fact]
    public async Task ImportPackageAsync_RefusesAPathWithInvalidCharacters()
    {
        var channel = ChannelAnswering(InstallJson());

        var ex = await Assert.ThrowsAsync<TranslationException>(() =>
            Create(channel).ImportPackageAsync(Path.Combine(_directory, "bad\0name.argosmodel"), CancellationToken.None));

        Assert.Equal(TranslationErrorKind.Failed, ex.Error.Kind);
        Assert.Empty(channel.Calls);
    }

    // ---- models.install: the request and the result ----

    [Fact]
    public async Task ImportPackageAsync_SendsTheFullPath_ReturnsThePackage_AndRaisesChangedOnce()
    {
        var path = PackageFile();
        var channel = ChannelAnswering(InstallJson(Package("de", "en", "1.9"), replaced: true));
        var catalog = Create(channel);
        var raised = new List<(object? Sender, int CallsSoFar)>();
        catalog.Changed += (sender, _) => raised.Add((sender, channel.Calls.Count));

        var package = await catalog.ImportPackageAsync(path, CancellationToken.None);

        var call = Assert.Single(channel.Calls);
        Assert.Equal("models.install", call.MessageType);
        Assert.Equal(["path"], call.Json.EnumerateObject().Select(p => p.Name));
        Assert.Equal(Path.GetFullPath(path), call.Json.GetProperty("path").GetString());
        Assert.Equal(TimeSpan.FromSeconds(180), call.Timeout);
        Assert.Equal(new TranslationPackageInfo("de", "en", "German", "English", "1.9", "translate-de_en"), package);
        var notification = Assert.Single(raised);
        Assert.Same(catalog, notification.Sender);
        Assert.Equal(1, notification.CallsSoFar); // raised after the worker answered
    }

    [Fact]
    public async Task ImportPackageAsync_AcceptsTheExtensionInAnyCase()
    {
        var path = PackageFile("TRANSLATE-DE_EN.ARGOSMODEL");
        var channel = ChannelAnswering(InstallJson());

        await Create(channel).ImportPackageAsync(path, CancellationToken.None);

        Assert.Single(channel.Calls);
    }

    [Fact]
    public async Task ImportPackageAsync_CanonicalisesThePathBeforeSendingIt()
    {
        var path = PackageFile();
        var withDots = Path.Combine(_directory, "sub", "..", Path.GetFileName(path));
        var channel = ChannelAnswering(InstallJson());

        await Create(channel).ImportPackageAsync(withDots, CancellationToken.None);

        Assert.Equal(path, Assert.Single(channel.Calls).Json.GetProperty("path").GetString());
    }

    [Theory]
    [InlineData("package_not_found", TranslationErrorKind.Failed)]
    [InlineData("invalid_package", TranslationErrorKind.Failed)]
    [InlineData("unsupported_package", TranslationErrorKind.Failed)]
    [InlineData("install_failed", TranslationErrorKind.Failed)]
    [InlineData("engine_unavailable", TranslationErrorKind.EngineUnavailable)]
    [InlineData("invalid_request", TranslationErrorKind.Failed)]
    public async Task ImportPackageAsync_MapsTheWorkersErrorCodes_AndDoesNotRaiseChanged(string code, TranslationErrorKind expected)
    {
        var path = PackageFile();
        var failure = new WorkerException(WorkerErrorKind.Rejected, "The worker rejected the request (" + code + "): internal detail.", code);
        var channel = new FakeWorkerRequestChannel();
        channel.Throw(failure);
        var catalog = Create(channel);
        var changed = 0;
        catalog.Changed += (_, _) => changed++;

        var ex = await Assert.ThrowsAsync<TranslationException>(() => catalog.ImportPackageAsync(path, CancellationToken.None));

        Assert.Equal(expected, ex.Error.Kind);
        Assert.Same(failure, ex.InnerException);
        Assert.False(string.IsNullOrWhiteSpace(ex.Message));
        Assert.DoesNotContain(code, ex.Message, StringComparison.Ordinal); // a readable sentence, not the raw code
        Assert.DoesNotContain(path, ex.Message, StringComparison.Ordinal);
        Assert.Equal(0, changed);
    }

    [Theory]
    [InlineData(WorkerErrorKind.NotConnected, null)]
    [InlineData(WorkerErrorKind.RequestTimeout, null)]
    [InlineData(WorkerErrorKind.ConnectionLost, null)]
    [InlineData(WorkerErrorKind.Rejected, "internal_error")]
    [InlineData(WorkerErrorKind.Rejected, "not_initialized")]
    public async Task ImportPackageAsync_LeavesOtherWorkerFailuresAsWorkerExceptions(WorkerErrorKind kind, string? code)
    {
        var path = PackageFile();
        var failure = new WorkerException(kind, "The worker failed.", code);
        var channel = new FakeWorkerRequestChannel();
        channel.Throw(failure);
        var catalog = Create(channel);
        var changed = 0;
        catalog.Changed += (_, _) => changed++;

        var ex = await Assert.ThrowsAsync<WorkerException>(() => catalog.ImportPackageAsync(path, CancellationToken.None));

        Assert.Same(failure, ex);
        Assert.Equal(0, changed);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("{}")]
    [InlineData("""{ "package": null }""")]
    [InlineData("""{ "package": { "from": "de", "to": "en" } }""")]
    [InlineData("""{ "package": { "from": "DE", "to": "en", "fromName": "a", "toName": "b", "version": "1", "packageName": "p" } }""")]
    public async Task ImportPackageAsync_RejectsAMalformedResult_AndDoesNotRaiseChanged(string? response)
    {
        var path = PackageFile();
        var catalog = Create(ChannelAnswering(response));
        var changed = 0;
        catalog.Changed += (_, _) => changed++;

        var ex = await Assert.ThrowsAsync<WorkerException>(() => catalog.ImportPackageAsync(path, CancellationToken.None));

        Assert.Equal(WorkerErrorKind.ProtocolViolation, ex.Kind);
        Assert.Equal(0, changed);
    }

    [Fact]
    public async Task ImportPackageAsync_WithACancelledToken_ThrowsWithoutAskingTheWorker()
    {
        var path = PackageFile();
        var channel = ChannelAnswering(InstallJson());
        var catalog = Create(channel);
        var changed = 0;
        catalog.Changed += (_, _) => changed++;
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => catalog.ImportPackageAsync(path, cts.Token));

        Assert.Empty(channel.Calls);
        Assert.Equal(0, changed);
    }

    [Fact]
    public async Task ImportPackageAsync_PropagatesACancellationFromTheChannel_WithoutRaisingChanged()
    {
        var path = PackageFile();
        using var cts = new CancellationTokenSource();
        var channel = new FakeWorkerRequestChannel();
        channel.Handler = (_, ct) =>
        {
            cts.Cancel();
            ct.ThrowIfCancellationRequested();
            return Task.FromResult<object?>(null);
        };
        var catalog = Create(channel);
        var changed = 0;
        catalog.Changed += (_, _) => changed++;

        var ex = await Assert.ThrowsAnyAsync<OperationCanceledException>(() => catalog.ImportPackageAsync(path, cts.Token));

        Assert.IsNotType<TranslationException>(ex);
        Assert.Equal(0, changed);
    }

    // ---- Changed ----

    [Fact]
    public void NotifyChanged_RaisesChanged_EachTimeItIsCalled()
    {
        var catalog = Create(new FakeWorkerRequestChannel());
        var senders = new List<object?>();
        catalog.Changed += (sender, _) => senders.Add(sender);

        catalog.NotifyChanged();
        catalog.NotifyChanged();

        Assert.Equal([catalog, catalog], senders);
    }

    [Fact]
    public void NotifyChanged_WithoutSubscribers_IsHarmless()
    {
        Create(new FakeWorkerRequestChannel()).NotifyChanged();
    }

    [Fact]
    public async Task Changed_AFaultySubscriberNeitherFailsTheImportNorStopsTheOthers()
    {
        var path = PackageFile();
        var catalog = Create(ChannelAnswering(InstallJson()));
        var reached = 0;
        catalog.Changed += (_, _) => throw new InvalidOperationException("subscriber bug");
        catalog.Changed += (_, _) => reached++;

        var package = await catalog.ImportPackageAsync(path, CancellationToken.None);

        Assert.Equal("de", package.From);
        Assert.Equal(1, reached);
    }

    [Fact]
    public async Task Changed_IsNotRaisedByReadingTheCatalog()
    {
        var channel = ChannelAnswering(CatalogJson());
        var catalog = Create(channel);
        var changed = 0;
        catalog.Changed += (_, _) => changed++;

        await catalog.GetCatalogAsync(CancellationToken.None);

        Assert.Equal(0, changed);
    }

    // ---- arguments ----

    [Fact]
    public void Constructor_RejectsANullChannel()
    {
        Assert.Throws<ArgumentNullException>(() => new WorkerModelCatalog(null!));
    }
}
