using GlyphLens.Core.Abstractions;
using GlyphLens.Core.Contracts;
using GlyphLens.Core.Models;
using GlyphLens.Core.Ocr;

namespace GlyphLens.Core.Tests.Ocr;

public sealed class LocalOcrAdvisorTests
{
    private static readonly OcrLanguage[] EnglishOnly = [new("en-US", "English (United States)")];
    private static readonly OcrLanguage[] WithChinese = [new("en-US", "English (United States)"), new("zh-Hans-CN", "Chinese (Simplified, China)")];
    private const string Local = OcrEngineSelector.LocalEngineTag;

    private static WorkerHealth HealthWithOcr(WorkerComponentState ocr) => new(
        WorkerComponentState.Ready,
        WorkerComponentState.Connected,
        WorkerComponentState.Initialized,
        ocr,
        WorkerComponentState.Installed,
        WorkerComponentState.Installed,
        WorkerComponentState.Installed,
        "0.2.0",
        ProtocolVersion.Current,
        "3.12.10",
        1);

    [Theory]
    [InlineData(WorkerComponentState.Ready, LocalOcrState.Ready)]
    [InlineData(WorkerComponentState.Installed, LocalOcrState.Ready)]
    [InlineData(WorkerComponentState.NotInstalled, LocalOcrState.ModelsMissing)]
    [InlineData(WorkerComponentState.Unavailable, LocalOcrState.RuntimeMissing)]
    [InlineData(WorkerComponentState.NotImplemented, LocalOcrState.RuntimeMissing)]
    [InlineData(WorkerComponentState.Unknown, LocalOcrState.Unknown)]
    public void Classify_MapsTheWorkersOcrState(WorkerComponentState reported, LocalOcrState expected) =>
        Assert.Equal(expected, LocalOcrAdvisor.Classify(HealthWithOcr(reported)));

    [Fact]
    public void Classify_WithoutAHealthReportIsUnknown() =>
        Assert.Equal(LocalOcrState.Unknown, LocalOcrAdvisor.Classify(null));

    [Theory]
    [InlineData("zh", true)]
    [InlineData("zt", true)]
    [InlineData("ZH", true)]
    [InlineData("en", false)]
    [InlineData("fr", false)]
    [InlineData(null, false)]
    public void IsChinese_RecognisesBothScripts(string? code, bool expected) =>
        Assert.Equal(expected, LocalOcrAdvisor.IsChinese(code));

    [Fact]
    public void ChineseSituation_ChosenSourceDecides()
    {
        Assert.True(LocalOcrAdvisor.IsChineseSituation("zh", []));
        Assert.True(LocalOcrAdvisor.IsChineseSituation("zt", ["fr"]));
        Assert.False(LocalOcrAdvisor.IsChineseSituation("fr", ["zh"]));
    }

    [Fact]
    public void ChineseSituation_AutomaticSourceNeedsAnInstalledChineseModel()
    {
        Assert.True(LocalOcrAdvisor.IsChineseSituation(null, ["fr", "zh"]));
        Assert.False(LocalOcrAdvisor.IsChineseSituation(null, ["fr"]));
        Assert.False(LocalOcrAdvisor.IsChineseSituation(null, []));
    }

    [Theory]
    [InlineData("en-US", false)]
    [InlineData("zh-Hans-CN", true)]
    [InlineData("zh-Hant-TW", true)]
    public void WindowsCanReadChinese_ForAChosenTag(string tag, bool expected) =>
        Assert.Equal(expected, LocalOcrAdvisor.WindowsCanReadChinese(tag, EnglishOnly));

    [Fact]
    public void WindowsCanReadChinese_AutomaticLooksAtTheInstalledLanguages()
    {
        Assert.False(LocalOcrAdvisor.WindowsCanReadChinese(null, EnglishOnly));
        Assert.True(LocalOcrAdvisor.WindowsCanReadChinese(null, WithChinese));
    }

    [Fact]
    public void NotAChineseSituation_NothingChanges()
    {
        var advice = LocalOcrAdvisor.Advise(false, null, false, LocalOcrState.Ready, EnglishOnly);

        Assert.Same(OcrAdvice.Nothing, advice);
    }

    [Fact]
    public void ReadyLocalEngine_IsSelectedWhenTheUserHasNotChosen()
    {
        var advice = LocalOcrAdvisor.Advise(true, null, false, LocalOcrState.Ready, EnglishOnly);

        Assert.True(advice.SwitchToLocal);
        Assert.Equal(OcrAdviceSeverity.Info, advice.Severity);
        Assert.Contains("local Chinese OCR", advice.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void ReadyLocalEngine_NeverOverridesAnExplicitChoice_AndExplainsAnEnglishOnlyChoice()
    {
        var advice = LocalOcrAdvisor.Advise(true, "en-US", true, LocalOcrState.Ready, EnglishOnly);

        Assert.False(advice.SwitchToLocal);
        Assert.Equal(OcrAdviceSeverity.Problem, advice.Severity);
        Assert.Contains("cannot read Chinese", advice.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void ReadyLocalEngine_AnExplicitChineseCapableWindowsChoiceIsLeftAlone()
    {
        var advice = LocalOcrAdvisor.Advise(true, "zh-Hans-CN", true, LocalOcrState.Ready, WithChinese);

        Assert.Same(OcrAdvice.Nothing, advice);
    }

    [Fact]
    public void ReadyLocalEngine_ADeliberateChoiceNeedsNoComment()
    {
        Assert.Same(OcrAdvice.Nothing, LocalOcrAdvisor.Advise(true, Local, true, LocalOcrState.Ready, EnglishOnly));
    }

    [Fact]
    public void ReadyLocalEngine_ThatWasSelectedAutomaticallyKeepsItsExplanation()
    {
        var advice = LocalOcrAdvisor.Advise(true, Local, false, LocalOcrState.Ready, EnglishOnly);

        Assert.False(advice.SwitchToLocal);
        Assert.Equal(OcrAdviceSeverity.Info, advice.Severity);
        Assert.Contains("local Chinese OCR", advice.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void MissingModels_NeverSwitch_AndAreAProblemWhenWindowsCannotReadChinese()
    {
        var advice = LocalOcrAdvisor.Advise(true, null, false, LocalOcrState.ModelsMissing, EnglishOnly);

        Assert.False(advice.SwitchToLocal);
        Assert.Equal(OcrAdviceSeverity.Problem, advice.Severity);
        Assert.Contains("models are not installed", advice.Message, StringComparison.Ordinal);
        Assert.Contains("Windows OCR cannot read Chinese", advice.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void MissingModels_AreOnlyInformationWhenWindowsCanReadChinese()
    {
        var advice = LocalOcrAdvisor.Advise(true, null, false, LocalOcrState.ModelsMissing, WithChinese);

        Assert.False(advice.SwitchToLocal);
        Assert.Equal(OcrAdviceSeverity.Info, advice.Severity);
        Assert.DoesNotContain("cannot read Chinese", advice.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void MissingRuntime_IsExplainedAndNeverSwitches()
    {
        var advice = LocalOcrAdvisor.Advise(true, null, false, LocalOcrState.RuntimeMissing, EnglishOnly);

        Assert.False(advice.SwitchToLocal);
        Assert.Equal(OcrAdviceSeverity.Problem, advice.Severity);
        Assert.Contains("OCR packages", advice.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void UnknownState_NeverSwitches_AndOnlyHintsWhenWindowsCannotReadChinese()
    {
        var hint = LocalOcrAdvisor.Advise(true, null, false, LocalOcrState.Unknown, EnglishOnly);
        var quiet = LocalOcrAdvisor.Advise(true, null, false, LocalOcrState.Unknown, WithChinese);

        Assert.False(hint.SwitchToLocal);
        Assert.Equal(OcrAdviceSeverity.Info, hint.Severity);
        Assert.Contains("Turn translation on", hint.Message, StringComparison.Ordinal);
        Assert.Same(OcrAdvice.Nothing, quiet);
    }

    [Fact]
    public void ABrokenLocalEngineThatTheUserSelected_StillGetsTheReason()
    {
        var advice = LocalOcrAdvisor.Advise(true, Local, true, LocalOcrState.ModelsMissing, EnglishOnly);

        Assert.False(advice.SwitchToLocal);
        Assert.Contains("models are not installed", advice.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("Windows OCR cannot read Chinese", advice.Message, StringComparison.Ordinal);
    }
}
