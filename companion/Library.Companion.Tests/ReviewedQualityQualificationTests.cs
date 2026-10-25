using Library.Execution;
using Xunit;

namespace Library.Companion.Tests;

public class ReviewedQualityQualificationTests
{
    static SamplePlan Baseline() => new(
        new(1, "stage", 12, ["jpn"], [new(1, "jpn", "main")], true, true, DerivedMedia.EncodeMode),
        1, new(1000, 10, "invented"),
        [new(0, "video", "h264", "", ["default"]), new(1, "audio", "dts", "jpn", ["original"])],
        "fixture", new string('a', 64), Encoding: DerivedMedia.SdrEncoding);

    [Fact]
    public void OnlyVideoQualityChangesAndBaselineIsUnmodified()
    {
        var plan = Baseline(); var original = ExecutionContract.Serialize(plan);
        var before = NativeMediaProcessor.Arguments(plan);
        var comparison = ReviewedQualityQualification.PrepareRf20(plan, true);
        Assert.Equal(before.Length, comparison.Arguments.Length);
        var changed = Enumerable.Range(0, before.Length).Where(i => before[i] != comparison.Arguments[i]).ToArray();
        Assert.Equal(new[] { Array.IndexOf(before, "-crf:v:0") + 1 }, changed);
        Assert.Equal("20", comparison.Arguments[changed.Single()]);
        Assert.Equal(original, ExecutionContract.Serialize(plan));
        Assert.Equal(DerivedMedia.Digest(original), comparison.BaselinePlanSha256);
        Assert.Equal(plan.Encoding!.PresetSha256, comparison.ValidationPlan.Encoding!.PresetSha256);
        Assert.Equal(20, comparison.ValidationPlan.Encoding.Crf);
        Assert.Equal(22, comparison.BaselineEncoding.Crf);
    }

    [Fact]
    public void ComparisonProfileCannotEnterProduction()
    {
        var result = ReviewedQualityQualification.PrepareRf20(Baseline(), true);
        Assert.Throws<ArgumentException>(() => DerivedMedia.ValidateProfile(result.ValidationPlan));
        Assert.Throws<ArgumentException>(() => NativeMediaProcessor.Arguments(result.ValidationPlan));
    }

    [Fact]
    public void ApprovalAndBoundedSourceReviewAreRequired()
    {
        var plan = Baseline();
        Assert.Throws<IOException>(() => ReviewedQualityQualification.PrepareRf20(plan, false));
        foreach (var request in new[] { plan.Request with { Seconds = 31 }, plan.Request with { ConfirmSdr = false }, plan.Request with { ConfirmSourceComplete = false } })
            Assert.Throws<IOException>(() => ReviewedQualityQualification.PrepareRf20(plan with { Request = request }, true));
    }

    [Fact]
    public void ChangedBaselineCannotAcquireComparisonLabel()
    {
        var plan = Baseline();
        Assert.Throws<ArgumentException>(() => ReviewedQualityQualification.PrepareRf20(plan with { Encoding = plan.Encoding! with { Crf = 21 } }, true));
        Assert.Throws<ArgumentException>(() => ReviewedQualityQualification.PrepareRf20(plan with { Encoding = plan.Encoding! with { Preset = "fast" } }, true));
    }
}
