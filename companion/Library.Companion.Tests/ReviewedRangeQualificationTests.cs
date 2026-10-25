using System.Security.Cryptography;
using System.Text.Json;
using Library.Execution;
using Xunit;
namespace Library.Companion.Tests;

public class ReviewedRangeQualificationTests
{
    static readonly byte[] Sps = HeaderQualificationTests.Sps(true, null);
    static readonly string SpsHash = Convert.ToHexStringLower(SHA256.HashData(Sps));
    static byte[] Annex(params byte[][] nals) => nals.SelectMany(n => new byte[] { 0, 0, 0, 1 }.Concat(n)).ToArray();
    static byte[] Window() => Annex(Sps, [0x68, 0xc0], [0x65, 0xbc], [0x41, 0xbc]);
    static JsonElement Probe() => JsonSerializer.SerializeToElement(new { streams = new[] { new { index = 0, codec_type = "video", codec_name = "h264", extradata_hash = "SHA256:" + new string('a', 64) } }, format = new { start_time = "0.000000" } });
    static HeaderQualification.Track Track() => new(1, 1, "V_MPEG4/ISO/AVC", false, null, new string('a', 64), [HeaderQualification.Sps(Sps)]);
    static readonly FileObservation Source = new(1000, 50, "invented-identity");
    [Fact] public void EverySliceResolvesToTheApprovedSps()
    { var result = ReviewedRangeQualification.InspectAnnexB(Window(), SpsHash, 2); Assert.Equal(2, result.Pictures); Assert.Equal(2, result.Slices); Assert.Equal(1, result.IdrSlices); }
    [Fact] public void LaterConflictingSpsIsRejected()
    { Assert.Throws<IOException>(() => ReviewedRangeQualification.InspectAnnexB(Annex(Sps, [0x68, 0xc0], [0x65, 0xbc], HeaderQualificationTests.Sps(true, true)), SpsHash, 1)); }
    [Fact] public void UnresolvedParametersAndNonIdrStartAreRejected()
    {
        Assert.Throws<IOException>(() => ReviewedRangeQualification.InspectAnnexB(Annex(Sps, [0x65, 0xbc]), SpsHash, 1));
        Assert.Throws<IOException>(() => ReviewedRangeQualification.InspectAnnexB(Annex(Sps, [0x68, 0xa0], [0x65, 0xbc]), SpsHash, 1));
        Assert.Throws<IOException>(() => ReviewedRangeQualification.InspectAnnexB(Annex(Sps, [0x68, 0xc0], [0x41, 0xbc]), SpsHash, 1));
    }
    [Fact] public void TruncatedUnsupportedAndShortWindowsAreRejected()
    {
        Assert.Throws<IOException>(() => ReviewedRangeQualification.InspectAnnexB(Annex(Sps, [0x68]), SpsHash, 1));
        Assert.Throws<IOException>(() => ReviewedRangeQualification.InspectAnnexB(Annex(Sps, [0x6f, 0xc0]), SpsHash, 1));
        Assert.Throws<IOException>(() => ReviewedRangeQualification.InspectAnnexB(Annex(Sps, [0x08, 0xc0]), SpsHash, 1));
        Assert.Throws<IOException>(() => ReviewedRangeQualification.InspectAnnexB(Annex(Sps, [0x68, 0, 0, 3, 5]), SpsHash, 1));
        Assert.Throws<IOException>(() => ReviewedRangeQualification.InspectAnnexB(Window(), SpsHash, 3));
        Assert.Throws<IOException>(() => ReviewedRangeQualification.InspectAnnexB([4, 5, 6, 7, 8, 9, 10, 11], SpsHash, 1));
    }
    [Fact] public void ReviewCannotTransferAcrossSourceProbeOrWindow()
    {
        var raw = Probe(); var review = ReviewedRangeQualification.Approve(Source, Source, raw, Track(), Window(), 10, 2, 2, true);
        var view = review.Apply(Source, raw, 10, 2); Assert.Equal("tv", DerivedMedia.Text(view.GetProperty("streams")[0], "color_range"));
        Assert.Equal("", DerivedMedia.Text(raw.GetProperty("streams")[0], "color_range"));
        Assert.Throws<IOException>(() => review.Apply(Source with { Ticks = 51 }, raw, 10, 2));
        Assert.Throws<IOException>(() => review.Apply(Source, view, 10, 2));
        Assert.Throws<IOException>(() => review.Apply(Source, raw, 11, 2));
        Assert.Throws<IOException>(() => review.Apply(Source, raw, 10, 3));
    }
    [Fact] public void ReviewRequiresExplicitApprovalAndNonconflictingContainer()
    {
        Assert.Throws<IOException>(() => ReviewedRangeQualification.Approve(Source, Source, Probe(), Track(), Window(), 10, 2, 2, false));
        Assert.Throws<IOException>(() => ReviewedRangeQualification.Approve(Source, Source with { Bytes = 999 }, Probe(), Track(), Window(), 10, 2, 2, true));
        Assert.Throws<IOException>(() => ReviewedRangeQualification.Approve(Source, Source, Probe(), Track() with { ColourPresent = true, Range = 2 }, Window(), 10, 2, 2, true));
    }
    [Fact] public void PgsCanvasComesFromCompletePresentationSegments()
    {
        byte[] pcs = [0x16, 0, 11, 7, 128, 4, 56, 0, 0, 0, 0, 0, 0, 0];
        Assert.Equal(new[] { 1920, 1080 }, ReviewedRangeQualification.PgsCanvas(pcs));
        Assert.Equal(new[] { 0, 0 }, ReviewedRangeQualification.PgsCanvas([0x80, 0, 0]));
        Assert.Throws<IOException>(() => ReviewedRangeQualification.PgsCanvas(pcs[..^1]));
        var conflict = pcs.ToArray(); conflict[3] = 6;
        Assert.Throws<IOException>(() => ReviewedRangeQualification.PgsCanvas(pcs.Concat(conflict).ToArray()));
        Assert.Throws<IOException>(() => ReviewedRangeQualification.PgsCanvas([0x13, 0, 0]));
    }
}
