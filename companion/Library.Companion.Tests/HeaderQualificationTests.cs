using System.Text;
using Xunit;
namespace Library.Companion.Tests;

public class HeaderQualificationTests
{
    internal static byte[] Sps(bool vui, bool? full, bool high = false, int spsId = 0, int firstScalingDelta = 0)
    {
        var bits = new StringBuilder(); void B(int n, int width) => bits.Append(Convert.ToString(n, 2).PadLeft(width, '0'));
        void U(int n) { var value = Convert.ToString(n + 1, 2); bits.Append('0', value.Length - 1); bits.Append(value); }
        B(high ? 100 : 66, 8); B(0, 8); B(40, 8); U(spsId);
        if (high) { U(1); U(0); U(0); B(0, 1); B(1, 1); B(1, 1); for (var i = 0; i < 16; i++) U(i == 0 ? (firstScalingDelta > 0 ? 2 * firstScalingDelta - 1 : -2 * firstScalingDelta) : 0); for (var i = 1; i < 8; i++) B(0, 1); }
        U(0); U(0); U(0); U(1); B(0, 1); U(3); U(3); B(1, 1); B(1, 1); B(0, 1); B(vui ? 1 : 0, 1);
        if (vui) { B(0, 1); B(0, 1); B(full.HasValue ? 1 : 0, 1); if (full.HasValue) { B(5, 3); B(full.Value ? 1 : 0, 1); B(1, 1); B(1, 8); B(1, 8); B(1, 8); } }
        B(1, 1); while (bits.Length % 8 != 0) B(0, 1);
        return new byte[] { 0x67 }.Concat(Enumerable.Range(0, bits.Length / 8).Select(i => Convert.ToByte(bits.ToString(i * 8, 8), 2))).ToArray();
    }
    static byte[] Avcc(byte[] sps) => new byte[] { 1, sps[1], 0, 40, 255, 225, (byte)(sps.Length >> 8), (byte)sps.Length }.Concat(sps).Concat(new byte[] { 0 }).ToArray();
    static byte[] E(byte[] id, params byte[][] children) { var data = children.SelectMany(c => c).ToArray(); Assert.True(data.Length < 127); return id.Concat(new[] { (byte)(128 | data.Length) }).Concat(data).ToArray(); }
    static byte[] Container(bool colour)
    {
        var entry = E([0xAE], E([0xD7], [1]), E([0x83], [1]), E([0x86], Encoding.ASCII.GetBytes("V_MPEG4/ISO/AVC")), E([0x63, 0xA2], Avcc(Sps(true, null))),
            E([0xE0], colour ? E([0x55, 0xB0], E([0x55, 0xB9], [0])) : []));
        return E([0x1A, 0x45, 0xDF, 0xA3]).Concat(E([0x18, 0x53, 0x80, 0x67], E([0x16, 0x54, 0xAE, 0x6B], entry))).ToArray();
    }
    [Theory] [InlineData(false)] [InlineData(true)]
    public void ExplicitFlagsRemainDistinctFromAbsent(bool full)
    {
        var result = HeaderQualification.Sps(Sps(true, full)); Assert.True(result.VuiPresent); Assert.True(result.SignalPresent); Assert.Equal(full, result.FullRange); Assert.Equal(1, result.Matrix);
        var unknown = HeaderQualification.Sps(Sps(true, null)); Assert.False(unknown.SignalPresent); Assert.Null(unknown.FullRange);
        Assert.False(HeaderQualification.Sps(Sps(false, null)).VuiPresent);
    }
    [Fact] public void HighProfileScalingListsDoNotShiftTheVuiRead()
    { var result = HeaderQualification.Sps(Sps(true, true, true)); Assert.Equal(100, result.Profile); Assert.True(result.FullRange); }
    [Theory] [InlineData(false)] [InlineData(true)]
    public void ContainerAbsentAndExplicitUnspecifiedAreDistinct(bool present)
    {
        var track = Assert.Single(HeaderQualification.Inspect(Container(present)));
        Assert.Equal(present, track.ColourPresent); Assert.Equal(present ? 0UL : null, track.Range); Assert.Null(Assert.Single(track.Sps).FullRange);
    }
    [Fact] public void MalformedAndOversizedInputsCannotProduceEvidence()
    {
        Assert.Throws<IOException>(() => HeaderQualification.Inspect(new byte[4 * 1024 * 1024 + 1]));
        Assert.Throws<IOException>(() => HeaderQualification.Inspect(Container(false)[..^1]));
        Assert.Throws<IOException>(() => HeaderQualification.Sps([0x67, 100, 0, 40]));
        Assert.Throws<IOException>(() => HeaderQualification.Sps([0x68, 66, 0, 40]));
        Assert.Throws<IOException>(() => HeaderQualification.Avcc([1, 66, 0, 40, 255, 225, 0, 50, 0]));
        Assert.Throws<IOException>(() => HeaderQualification.Sps([0x67, 0, 0, 3]));
    }
    [Fact] public void UnsupportedProfilesAndInvalidSyntaxAreRefused()
    {
        var unsupported = Sps(true, true); unsupported[1] = 255;
        Assert.Throws<IOException>(() => HeaderQualification.Sps(unsupported));
        var nonReference = Sps(true, true); nonReference[0] = 7;
        Assert.Throws<IOException>(() => HeaderQualification.Sps(nonReference));
        Assert.Throws<IOException>(() => HeaderQualification.Sps(Sps(true, true, spsId: 32)));
        Assert.Throws<IOException>(() => HeaderQualification.Sps(Sps(true, true, true, firstScalingDelta: -129)));
        Assert.Throws<IOException>(() => HeaderQualification.Sps(Sps(true, true, true, firstScalingDelta: 128)));
    }
}
