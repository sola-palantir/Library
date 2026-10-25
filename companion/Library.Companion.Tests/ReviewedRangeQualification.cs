using System.Globalization;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;
using Library.Execution;

namespace Library.Companion.Tests;

// Offline qualification only. There is deliberately no API/worker activation
// path to this review: production Select still refuses undeclared range.
public static class ReviewedRangeQualification
{
    public record WindowProof(string Sha256, int Bytes, int Nals, int Sps, int Pps, int Slices, int Pictures, int IdrSlices);
    public sealed class Review
    {
        public FileObservation Source { get; }
        public string RawProbeSignature { get; }
        public string ConfigurationSha256 { get; }
        public string SpsSha256 { get; }
        public double Start { get; }
        public int Seconds { get; }
        public WindowProof Window { get; }
        public string Interpretation => "reviewed-h264-absent-full-range-flag-default-0";
        internal Review(FileObservation source, string signature, string configuration, string sps, double start, int seconds, WindowProof window)
        { Source = source; RawProbeSignature = signature; ConfigurationSha256 = configuration; SpsSha256 = sps; Start = start; Seconds = seconds; Window = window; }
        public JsonElement Apply(FileObservation observed, JsonElement raw, double start, int seconds)
        {
            if (observed != Source || DerivedMedia.ProbeSignature(raw) != RawProbeSignature || start != Start || seconds != Seconds)
                throw new IOException("Range review does not belong to this source, inventory and window.");
            var view = JsonNode.Parse(raw.GetRawText())!;
            var video = view["streams"]!.AsArray().Single(v => v!["codec_type"]!.GetValue<string>() == "video")!;
            video["color_range"] = "tv";
            // This explicitly labeled comparison view establishes the selected
            // window's origin; the raw probe is retained and never rewritten.
            view["format"]!["start_time"] = start.ToString("F6", CultureInfo.InvariantCulture);
            return JsonSerializer.SerializeToElement(view);
        }
    }
    public static Review Approve(FileObservation approvedSource, FileObservation observed, JsonElement raw,
        HeaderQualification.Track track, byte[] annexB, double start, int seconds, int minimumPictures, bool explicitlyReviewed)
    {
        if (!explicitlyReviewed || approvedSource != observed || !double.IsFinite(start) || start < 0 || seconds is < 1 or > 30 || minimumPictures < 1)
            throw new IOException("Explicit matching source/window review required.");
        var video = raw.GetProperty("streams").EnumerateArray().Single(v => DerivedMedia.Text(v, "codec_type") == "video");
        if (DerivedMedia.Text(video, "codec_name") != "h264" || DerivedMedia.Text(video, "color_range") is not ("" or "unknown" or "unspecified") ||
            track.ColourPresent || track.Range != null || track.Codec != "V_MPEG4/ISO/AVC" || track.Sps.Length != 1 ||
            DerivedMedia.Text(video, "extradata_hash") != "SHA256:" + track.PrivateSha256)
            throw new IOException("Container/probe evidence conflicts with the narrow default interpretation.");
        var sps = track.Sps.Single();
        if (sps.SignalPresent || sps.FullRange != null || sps.Primaries != null || sps.Transfer != null || sps.Matrix != null)
            throw new IOException("This review applies only to the inspected absent-signal syntax.");
        var proof = InspectAnnexB(annexB, sps.SpsSha256, minimumPictures);
        return new(observed, DerivedMedia.ProbeSignature(raw), track.PrivateSha256, sps.SpsSha256, start, seconds, proof);
    }
    public static WindowProof InspectAnnexB(byte[] bytes, string approvedSpsSha256, int minimumPictures)
    {
        if (bytes.Length is < 8 or > 128 * 1024 * 1024 || minimumPictures is < 1 or > 3600) throw new IOException("Bounded complete AVC window required.");
        ExecutionContract.Hash(approvedSpsSha256);
        var spsIds = new HashSet<int>(); var ppsIds = new Dictionary<int, int>();
        int nals = 0, spsCount = 0, ppsCount = 0, slices = 0, pictures = 0, idrs = 0;
        var begin = -1;
        for (var i = 0; i <= bytes.Length; i++)
        {
            var boundary = i == bytes.Length || i + 2 < bytes.Length && bytes[i] == 0 && bytes[i + 1] == 0 && bytes[i + 2] == 1;
            if (!boundary) continue;
            if (begin >= 0)
            {
                var end = i; while (end > begin && bytes[end - 1] == 0) end--;
                if (end <= begin || ++nals > 20000) throw new IOException("Empty or excessive AVC NAL units.");
                var nal = bytes.AsSpan(begin, end - begin); var type = nal[0] & 31;
                if ((nal[0] & 128) != 0) throw new IOException("Forbidden AVC NAL bit.");
                if (type is 5 or 7 or 8 && (nal[0] & 96) == 0) throw new IOException("Invalid reference NAL header.");
                if (type == 7)
                {
                    var sps = HeaderQualification.Sps(nal.ToArray());
                    if (sps.SpsSha256 != approvedSpsSha256 || sps.SignalPresent || sps.FullRange != null)
                        throw new IOException("In-band SPS conflicts with the approved configuration.");
                    spsIds.Add(sps.SpsId); spsCount++;
                }
                else if (type == 8)
                {
                    var reader = new PrefixBits(nal[1..]); var pps = reader.Ue(); var sps = reader.Ue();
                    if (pps > 255 || !spsIds.Contains(sps)) throw new IOException("PPS references unverified SPS.");
                    ppsIds[pps] = sps; ppsCount++;
                }
                else if (type is 1 or 5)
                {
                    var reader = new PrefixBits(nal[1..]); var firstMb = reader.Ue(); var sliceType = reader.Ue(); var pps = reader.Ue();
                    if (sliceType > 9 || !ppsIds.TryGetValue(pps, out var sps) || !spsIds.Contains(sps) || slices == 0 && (type != 5 || firstMb != 0))
                        throw new IOException("Slice does not start at an IDR or references unverified parameters.");
                    slices++; if (firstMb == 0) pictures++; if (type == 5) idrs++;
                }
                else if (type is not (6 or 9 or 10 or 11 or 12)) throw new IOException("Unsupported AVC NAL type in range qualification.");
            }
            else if (bytes.AsSpan(0, i).IndexOfAnyExcept((byte)0) >= 0) throw new IOException("Data precedes AVC start code.");
            if (i == bytes.Length) break;
            begin = i + 3; i += 2;
        }
        if (spsCount == 0 || ppsCount == 0 || pictures < minimumPictures || idrs == 0) throw new IOException("Incomplete parameter-set/window coverage.");
        return new(Convert.ToHexStringLower(SHA256.HashData(bytes)), bytes.Length, nals, spsCount, ppsCount, slices, pictures, idrs);
    }
    // Qualification evidence from the PCS video descriptor, not guessed from
    // the movie dimensions. Callers must verify packet hash/size against source.
    public static int[] PgsCanvas(byte[] packet)
    {
        if (packet.Length is < 3 or > 1024 * 1024) throw new IOException("Bounded PGS packet required.");
        int pos = 0, width = 0, height = 0;
        while (pos < packet.Length)
        {
            if (pos + 3 > packet.Length) throw new IOException("Truncated PGS segment.");
            var type = packet[pos++]; var size = packet[pos++] << 8; size |= packet[pos++];
            if (pos + size > packet.Length || type is not (0x14 or 0x15 or 0x16 or 0x17 or 0x80)) throw new IOException("Invalid PGS segment.");
            if (type == 0x16)
            {
                if (size < 11) throw new IOException("Truncated PGS presentation.");
                var w = packet[pos] * 256 + packet[pos + 1]; var h = packet[pos + 2] * 256 + packet[pos + 3];
                if (w is < 1 or > 8192 || h is < 1 or > 8192 || width != 0 && (width != w || height != h)) throw new IOException("Conflicting PGS canvas.");
                width = w; height = h;
            }
            pos += size;
        }
        return [width, height];
    }
    sealed class PrefixBits
    {
        readonly byte[] bytes; int bit;
        public PrefixBits(ReadOnlySpan<byte> ebsp)
        {
            // Only the bounded first three Exp-Golomb fields are needed, never
            // allocate/copy a compressed picture. Preserve emulation prevention.
            var result = new List<byte>(); var zeros = 0;
            var prefix = ebsp[..Math.Min(ebsp.Length, 64)];
            for (var i = 0; i < prefix.Length; i++)
            {
                var b = prefix[i];
                if (zeros >= 2 && b == 3)
                { if (i + 1 >= ebsp.Length || ebsp[i + 1] > 3) throw new IOException("Invalid AVC emulation prevention."); zeros = 0; continue; }
                result.Add(b); zeros = b == 0 ? zeros + 1 : 0;
            }
            bytes = result.ToArray();
        }
        int Bit() { if (bit >= bytes.Length * 8) throw new IOException("Truncated AVC prefix."); var value = bytes[bit / 8] >> (7 - bit % 8) & 1; bit++; return value; }
        public int Ue() { var zeros = 0; while (Bit() == 0) if (++zeros > 20) throw new IOException("Unbounded AVC prefix."); var value = 0; for (var n = 0; n < zeros; n++) value = value * 2 + Bit(); return (1 << zeros) - 1 + value; }
    }
}
