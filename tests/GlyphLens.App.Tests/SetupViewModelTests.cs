using GlyphLens.App.ViewModels;
using GlyphLens.Core.Abstractions;
using GlyphLens.Core.Setup;

namespace GlyphLens.App.Tests;

public sealed class SetupViewModelTests
{
    /// <summary>A scripted setup: reports the plan's steps and ends as told.</summary>
    internal sealed class FakeSetup : IEnvironmentSetup
    {
        public string? PlanProblem { get; set; }

        public Func<SetupOptions, IProgress<SetupProgress>?, CancellationToken, Task<SetupResult>>? Script { get; set; }

        public Action? OnRun { get; set; }

        public List<SetupOptions> Runs { get; } = [];

        public SetupPlan Plan(SetupOptions options)
        {
            if (PlanProblem is { } problem)
            {
                throw new SetupException(SetupStepId.Prepare, problem);
            }

            var files = new List<PinnedFile> { SetupManifest.Uv };
            files.AddRange(SetupManifest.OcrFiles.Select(f => f.File));
            files.Add(SetupManifest.SimplifiedChineseModel);
            var steps = new List<SetupStepId> { SetupStepId.Prepare, SetupStepId.Uv, SetupStepId.Python, SetupStepId.Packages, SetupStepId.OcrModels, SetupStepId.Worker, SetupStepId.SimplifiedModel };
            if (options.IncludeTraditional)
            {
                files.Add(SetupManifest.TraditionalChineseModel);
                steps.Add(SetupStepId.TraditionalModel);
            }

            steps.Add(SetupStepId.Check);
            return new SetupPlan(steps, files, files.Sum(f => f.Size) + 1000, @"C:\Users\u\AppData\Local\GlyphLens\runtime", @"C:\Users\u\AppData\Local\GlyphLens\models", @"C:\Users\u\AppData\Local\GlyphLens\runtime\setup.log", 70);
        }

        public async Task<SetupResult> RunAsync(SetupOptions options, IProgress<SetupProgress>? progress, CancellationToken cancellationToken)
        {
            Runs.Add(options);
            OnRun?.Invoke();
            if (Script is not null)
            {
                return await Script(options, progress, cancellationToken);
            }

            foreach (var step in Plan(options).Steps)
            {
                progress?.Report(new SetupProgress(step, SetupStepState.Running, $"{step}…"));
                progress?.Report(new SetupProgress(step, SetupStepState.Done, "Done."));
            }

            return new SetupResult([], []);
        }
    }

    private static (SetupViewModel Vm, FakeSetup Setup, List<string> Logs) Create(bool needed = true)
    {
        var setup = new FakeSetup();
        var logs = new List<string>();
        var vm = new SetupViewModel(setup, uiContext: null, logs.Add);
        vm.SetNeeded(needed);
        return (vm, setup, logs);
    }

    [Fact]
    public void TheOffer_FollowsWhetherSomethingIsMissing_AndNothingRunsOnItsOwn()
    {
        var (vm, setup, _) = Create(needed: false);
        Assert.Equal(SetupPanelState.Hidden, vm.State);
        Assert.False(vm.IsVisible);

        vm.SetNeeded(true);
        Assert.Equal(SetupPanelState.Offer, vm.State);
        Assert.True(vm.ShowConsentCommand.CanExecute(null));
        Assert.False(vm.StartCommand.CanExecute(null)); // only from the consent panel

        vm.SetNeeded(false);
        Assert.Equal(SetupPanelState.Hidden, vm.State);
        Assert.Empty(setup.Runs);
    }

    [Fact]
    public void TheConsentPanel_SaysWhatWhereHowMuchAndUnderWhichLicence()
    {
        var (vm, setup, _) = Create();

        vm.ShowConsentCommand.Execute(null);

        Assert.Equal(SetupPanelState.Consent, vm.State);
        Assert.Empty(setup.Runs);
        Assert.True(vm.IncludeTraditional);
        Assert.Equal("Also install Traditional Chinese (+71 MB)", vm.TraditionalLabel);
        Assert.Contains(vm.Downloads, d => d.Host == "github.com" && d.What.Contains("uv 0.13.7", StringComparison.Ordinal) && d.Licence == "MIT or Apache-2.0");
        Assert.Contains(vm.Downloads, d => d.Host == "releases.astral.sh");
        Assert.Contains(vm.Downloads, d => d.Host == "pypi.org" && d.What.StartsWith("70 Python packages", StringComparison.Ordinal));
        Assert.Contains(vm.Downloads, d => d.Host == "huggingface.co" && d.Licence == "Apache-2.0");
        Assert.Contains(vm.Downloads, d => d.Host == "argos-net.com" && d.What.StartsWith("Simplified", StringComparison.Ordinal) && d.Licence == "CC-BY 4.0");
        Assert.Contains(vm.Downloads, d => d.What.StartsWith("Traditional", StringComparison.Ordinal));
        Assert.StartsWith("About ", vm.TotalText, StringComparison.Ordinal);
        Assert.Contains(@"GlyphLens\runtime", vm.FoldersText, StringComparison.Ordinal);
        Assert.Contains(@"GlyphLens\models", vm.FoldersText, StringComparison.Ordinal);
        Assert.Equal("Needs an internet connection. Nothing from your screen is sent. You can cancel at any time.", vm.ConsentNote);
        Assert.Contains("CC-BY 4.0", vm.LicenceNote, StringComparison.Ordinal);
        Assert.Contains("Apache-2.0", vm.LicenceNote, StringComparison.Ordinal);
        Assert.All(vm.Downloads, d => Assert.False(string.IsNullOrWhiteSpace(d.AutomationName)));
    }

    [Fact]
    public void UncheckingTraditional_RemovesItFromTheListAndTheTotal()
    {
        var (vm, _, _) = Create();
        vm.ShowConsentCommand.Execute(null);
        string withTraditional = vm.TotalText;

        vm.IncludeTraditional = false;

        Assert.DoesNotContain(vm.Downloads, d => d.What.StartsWith("Traditional", StringComparison.Ordinal));
        Assert.NotEqual(withTraditional, vm.TotalText);
    }

    [Fact]
    public void CancellingTheConsent_GoesBackToTheOffer_WithoutRunning()
    {
        var (vm, setup, _) = Create();
        vm.ShowConsentCommand.Execute(null);

        vm.CancelConsentCommand.Execute(null);

        Assert.Equal(SetupPanelState.Offer, vm.State);
        Assert.Empty(setup.Runs);
    }

    [Fact]
    public async Task Confirming_RunsTheSteps_AndEndsSucceeded_WithEveryStepDoneOrSkipped()
    {
        var (vm, setup, _) = Create();
        setup.Script = (options, progress, _) =>
        {
            progress?.Report(new SetupProgress(SetupStepId.Prepare, SetupStepState.Running, "x"));
            return Task.FromResult(new SetupResult([SetupStepId.Uv], []));
        };
        int completed = 0;
        vm.Completed += (_, _) => completed++;
        vm.ShowConsentCommand.Execute(null);
        vm.IncludeTraditional = false;

        await vm.StartCommand.ExecuteAsync();

        Assert.Equal(SetupPanelState.Succeeded, vm.State);
        Assert.Equal([new SetupOptions(false)], setup.Runs);
        Assert.Equal(1, completed);
        Assert.Equal(SetupStepState.Skipped, vm.Steps.Single(s => s.Id == SetupStepId.Uv).State);
        Assert.All(vm.Steps.Where(s => s.Id != SetupStepId.Uv), s => Assert.Equal(SetupStepState.Done, s.State));
        Assert.Contains("works offline", vm.ResultText, StringComparison.Ordinal);
        Assert.True(vm.CloseCommand.CanExecute(null));
    }

    [Fact]
    public async Task WhileRunning_TheStepListShowsDoneCurrentAndPending_AndDownloadProgress()
    {
        var (vm, setup, _) = Create();
        var gate = new TaskCompletionSource<SetupResult>();
        setup.Script = (_, progress, _) =>
        {
            progress?.Report(new SetupProgress(SetupStepId.Prepare, SetupStepState.Running, "Checking"));
            progress?.Report(new SetupProgress(SetupStepId.Prepare, SetupStepState.Done, "Done."));
            progress?.Report(new SetupProgress(SetupStepId.Uv, SetupStepState.Running, "Downloading uv from github.com…", 5 * 1024 * 1024, 10 * 1024 * 1024));
            return gate.Task;
        };
        vm.ShowConsentCommand.Execute(null);

        var run = vm.StartCommand.ExecuteAsync();

        Assert.Equal(SetupPanelState.Running, vm.State);
        Assert.Equal(SetupStepState.Done, vm.Steps[0].State);
        Assert.Equal(SetupStepState.Running, vm.Steps[1].State);
        Assert.Equal(SetupStepState.Pending, vm.Steps[2].State);
        Assert.Equal("Get uv 0.13.7 (installs Python): in progress", vm.Steps[1].AutomationName);
        Assert.Equal("Downloading uv from github.com…", vm.Message);
        Assert.False(vm.IsProgressIndeterminate);
        Assert.Equal(50, vm.ProgressPercent, 3);
        Assert.Equal("5.0 MB of 10.0 MB", vm.ProgressText);
        Assert.True(vm.CancelCommand.CanExecute(null));
        Assert.False(vm.ShowConsentCommand.CanExecute(null));

        gate.SetResult(new SetupResult([], []));
        await run;
    }

    [Fact]
    public async Task AFailure_NamesTheStep_ShowsTheLog_AndTryAgainResumes()
    {
        var (vm, setup, logs) = Create();
        setup.Script = (_, progress, _) =>
        {
            progress?.Report(new SetupProgress(SetupStepId.Packages, SetupStepState.Running, "Installing"));
            throw new SetupException(SetupStepId.Packages, "The OCR and translation packages could not be downloaded or installed. Check the internet connection.");
        };
        vm.ShowConsentCommand.Execute(null);

        await vm.StartCommand.ExecuteAsync();

        Assert.Equal(SetupPanelState.Failed, vm.State);
        Assert.Equal("Install the OCR and translation packages", vm.FailedStep);
        Assert.Contains("Check the internet connection", vm.ErrorMessage, StringComparison.Ordinal);
        Assert.Equal(SetupStepState.Failed, vm.Steps.Single(s => s.Id == SetupStepId.Packages).State);
        Assert.EndsWith("setup.log", vm.LogPath, StringComparison.Ordinal);
        vm.ShowLogCommand.Execute(null);
        Assert.Equal([vm.LogPath], logs);
        Assert.True(vm.RetryCommand.CanExecute(null));

        setup.Script = null;
        await vm.RetryCommand.ExecuteAsync();

        Assert.Equal(SetupPanelState.Succeeded, vm.State);
        Assert.Equal(2, setup.Runs.Count);
        Assert.Null(vm.ErrorMessage);
    }

    [Fact]
    public async Task Cancelling_StopsTheRun_AndReturnsToTheOfferWithANote()
    {
        var (vm, setup, _) = Create();
        setup.Script = async (_, progress, token) =>
        {
            progress?.Report(new SetupProgress(SetupStepId.Packages, SetupStepState.Running, "Installing"));
            await Task.Delay(Timeout.Infinite, token);
            return new SetupResult([], []);
        };
        int completed = 0;
        vm.Completed += (_, _) => completed++;
        vm.ShowConsentCommand.Execute(null);

        var run = vm.StartCommand.ExecuteAsync();
        vm.CancelCommand.Execute(null);
        await run;

        Assert.Equal(SetupPanelState.Offer, vm.State);
        Assert.Contains("Cancelled", vm.Notice, StringComparison.Ordinal);
        Assert.Equal(0, completed);
        Assert.DoesNotContain(vm.Steps, s => s.State == SetupStepState.Running);
        Assert.True(vm.ShowConsentCommand.CanExecute(null)); // the app stays usable: it can be started again
    }

    [Fact]
    public async Task AnUnexpectedError_IsShownAsAFailure_NotAsACrash()
    {
        var (vm, setup, _) = Create();
        setup.Script = (_, _, _) => throw new FormatException("boom");
        vm.ShowConsentCommand.Execute(null);

        await vm.StartCommand.ExecuteAsync();

        Assert.Equal(SetupPanelState.Failed, vm.State);
        Assert.Contains("FormatException", vm.ErrorMessage, StringComparison.Ordinal);
    }

    [Fact]
    public void AnUnusableRuntimeFolder_IsShown_AndTheDownloadCannotStart()
    {
        var setup = new FakeSetup { PlanProblem = "GLYPHLENS_RUNTIME_DIR is not an absolute local folder, so the setup will not use it." };
        var vm = new SetupViewModel(setup, uiContext: null);
        vm.SetNeeded(true);

        vm.ShowConsentCommand.Execute(null);

        Assert.True(vm.HasPlanProblem);
        Assert.Contains("GLYPHLENS_RUNTIME_DIR", vm.PlanProblem, StringComparison.Ordinal);
        Assert.False(vm.StartCommand.CanExecute(null));
        Assert.Empty(vm.Downloads);
    }

    [Fact]
    public async Task ClosingAfterSuccess_HidesThePanel_WhenNothingIsMissingAnyMore()
    {
        var (vm, _, _) = Create();
        vm.ShowConsentCommand.Execute(null);
        await vm.StartCommand.ExecuteAsync();
        vm.SetNeeded(false);
        Assert.Equal(SetupPanelState.Succeeded, vm.State);

        vm.CloseCommand.Execute(null);

        Assert.Equal(SetupPanelState.Hidden, vm.State);
    }

    [Fact]
    public void EveryStepHasATitle()
    {
        Assert.All(Enum.GetValues<SetupStepId>(), s => Assert.NotEqual(s.ToString(), SetupViewModel.TitleOf(s)));
    }
}
