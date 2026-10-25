using Library.Execution;

namespace Library.Companion.Tests;

// Explicit offline A/B qualification only. Production still accepts only its
// original immutable RF22 profile; no API/worker activation uses this helper.
public static class ReviewedQualityQualification
{
    public record Comparison(SamplePlan ValidationPlan, SampleEncoding BaselineEncoding,
        string BaselinePlanSha256, string[] Arguments);

    public static Comparison PrepareRf20(SamplePlan baseline, bool explicitlyReviewed)
    {
        DerivedMedia.ValidateProfile(baseline);
        if (!explicitlyReviewed || baseline.Encoding != DerivedMedia.SdrEncoding ||
            baseline.Request.Seconds is < 1 or > 30 || !baseline.Request.ConfirmSdr ||
            !baseline.Request.ConfirmSourceComplete)
            throw new IOException("Explicit bounded RF20 comparison of the original RF22 profile required.");

        var args = NativeMediaProcessor.Arguments(baseline);
        var indices = Enumerable.Range(0, args.Length).Where(i => args[i] == "-crf:v:0").ToArray();
        if (indices.Length != 1 || indices[0] + 1 >= args.Length || args[indices[0] + 1] != "22")
            throw new IOException("Unexpected baseline video-quality arguments.");
        args[indices[0] + 1] = "20";
        var encoding = baseline.Encoding with { Id = "qualification-only-1080-rf20-slow-v1", Crf = 20 };
        return new(baseline with { Encoding = encoding }, baseline.Encoding,
            DerivedMedia.Digest(ExecutionContract.Serialize(baseline)), args);
    }
}
