using System.Security.Cryptography;
using System.Text;
using Library.Execution;

namespace Library.Companion.Tests;

// Bounded qualification-only metadata reader. It never rewrites source data,
// authorizes an encode or infers mastering intent. Not a full Matroska validator.
public static class HeaderQualification
{
    public record AvcRange(int Profile, int SpsId, bool VuiPresent, bool SignalPresent, bool? FullRange,
        int? Primaries, int? Transfer, int? Matrix, string SpsSha256);
    public record Track(ulong Number, ulong Type, string Codec, bool ColourPresent, ulong? Range, string PrivateSha256, AvcRange[] Sps);
    record Element(ulong Id, int Start, int End);
    static (ulong Value, int Bytes, bool Unknown) Vint(byte[] data, ref int pos, bool id)
    {
        if (pos >= data.Length || data[pos] == 0) throw new IOException("Truncated/invalid EBML integer.");
        var first = data[pos++]; var marker = 0x80; var length = 1;
        while ((first & marker) == 0) { marker >>= 1; length++; }
        if (length > (id ? 4 : 8) || pos + length - 1 > data.Length) throw new IOException("Invalid EBML length.");
        ulong value = (ulong)(id ? first : first & (marker - 1));
        var unknown = !id && value == (ulong)(marker - 1);
        for (var i = 1; i < length; i++) { var b = data[pos++]; value = (value << 8) | b; unknown &= b == 255; }
        return (value, length, unknown);
    }
    static Element Next(byte[] data, ref int pos, int parentEnd, bool segment = false)
    {
        var id = Vint(data, ref pos, true).Value; var size = Vint(data, ref pos, false);
        if (pos > parentEnd) throw new IOException("Element header extends outside its bounded parent.");
        if (size.Unknown && (!segment || id != 0x18538067)) throw new IOException("Unknown-sized metadata is unsupported.");
        var end = size.Unknown ? parentEnd : checked((long)pos + (long)size.Value);
        if (!segment && end > parentEnd) throw new IOException("Metadata extends outside its bounded parent.");
        return new(id, pos, (int)Math.Min(end, parentEnd));
    }
    static IEnumerable<Element> Children(byte[] data, Element parent)
    {
        var pos = parent.Start; var count = 0;
        while (pos < parent.End) { if (++count > 2048) throw new IOException("Metadata element count exceeded."); var item = Next(data, ref pos, parent.End); yield return item; pos = item.End; }
    }
    static ulong Integer(byte[] data, Element value)
    {
        if (value.End - value.Start is < 1 or > 8) throw new IOException("Invalid scalar length.");
        ulong number = 0; for (var i = value.Start; i < value.End; i++) number = (number << 8) | data[i]; return number;
    }
    public static Track[] Inspect(byte[] prefix)
    {
        if (prefix.Length is < 16 or > 4 * 1024 * 1024) throw new IOException("Bounded header prefix required.");
        var pos = 0; var root = Next(prefix, ref pos, prefix.Length);
        if (root.Id != 0x1A45DFA3) throw new IOException("Expected Matroska EBML header.");
        pos = root.End; var segment = Next(prefix, ref pos, prefix.Length, true);
        if (segment.Id != 0x18538067) throw new IOException("Expected Segment.");
        Element? tracks = null; pos = segment.Start;
        for (var n = 0; n < 128 && pos < segment.End; n++)
        {
            // Stop at the first cluster: no compressed media is parsed here.
            var idPos = pos; var id = Vint(prefix, ref idPos, true).Value;
            if (id == 0x1F43B675) break;
            var item = Next(prefix, ref pos, segment.End);
            if (item.Id == 0x1654AE6B) { tracks = item; break; } pos = item.End;
        }
        if (tracks == null) throw new IOException("Tracks not found before media within bounded prefix.");
        var result = new List<Track>();
        foreach (var entry in Children(prefix, tracks).Where(e => e.Id == 0xAE))
        {
            if (result.Count >= 256) throw new IOException("Too many tracks.");
            var fields = Children(prefix, entry).ToArray();
            Element? One(ulong id) { var found = fields.Where(e => e.Id == id).ToArray(); if (found.Length > 1) throw new IOException("Duplicate track field."); return found.SingleOrDefault(); }
            var number = One(0xD7) ?? throw new IOException("Missing track number."); var type = One(0x83) ?? throw new IOException("Missing track type.");
            var codec = One(0x86) ?? throw new IOException("Missing codec."); var privateData = One(0x63A2);
            if (privateData != null && privateData.End - privateData.Start > 65536) throw new IOException("Codec private data too large.");
            var bytes = privateData == null ? [] : prefix[privateData.Start..privateData.End];
            var video = One(0xE0); var colour = video == null ? null : Children(prefix, video).SingleOrDefault(e => e.Id == 0x55B0);
            var range = colour == null ? null : Children(prefix, colour).SingleOrDefault(e => e.Id == 0x55B9);
            var codecName = Encoding.UTF8.GetString(prefix[codec.Start..codec.End]);
            result.Add(new(Integer(prefix, number), Integer(prefix, type), codecName, colour != null, range == null ? null : Integer(prefix, range),
                Convert.ToHexStringLower(SHA256.HashData(bytes)), codecName == "V_MPEG4/ISO/AVC" ? Avcc(bytes) : []));
        }
        if (result.Select(t => t.Number).Distinct().Count() != result.Count) throw new IOException("Duplicate track number.");
        return result.ToArray();
    }
    public static AvcRange[] Avcc(byte[] data)
    {
        if (data.Length is < 7 or > 65536 || data[0] != 1) throw new IOException("Invalid AVC configuration.");
        var pos = 6; var count = data[5] & 31; if (count == 0) throw new IOException("Missing SPS.");
        var result = new List<AvcRange>();
        for (var i = 0; i < count; i++)
        {
            if (pos + 2 > data.Length) throw new IOException("Truncated SPS length.");
            var size = (data[pos] << 8) | data[pos + 1]; pos += 2;
            if (size == 0 || pos + size > data.Length) throw new IOException("Truncated SPS.");
            result.Add(Sps(data[pos..(pos + size)])); pos += size;
        }
        return result.ToArray();
    }
    public static AvcRange Sps(byte[] nal)
    {
        if (nal.Length is < 4 or > 65536 || (nal[0] & 0x9F) != 7 || (nal[0] & 0x60) == 0) throw new IOException("Expected reference SPS NAL.");
        var rbsp = new List<byte>(); var zeros = 0;
        for (var i = 1; i < nal.Length; i++)
        {
            var b = nal[i];
            if (zeros >= 2 && b == 3) { if (i + 1 == nal.Length || nal[i + 1] > 3) throw new IOException("Invalid emulation prevention."); zeros = 0; continue; }
            rbsp.Add(b); zeros = b == 0 ? zeros + 1 : 0;
        }
        var bits = new Bits(rbsp.ToArray()); var profile = bits.Read(8); bits.Read(8); bits.Read(8); var id = bits.Ue(); var chroma = 1;
        var extended = new[] { 100, 110, 122, 244, 44, 83, 86, 118, 128, 138, 139, 134, 135 }.Contains(profile);
        if (!extended && profile is not (66 or 77 or 88)) throw new IOException("Unsupported SPS profile.");
        if (id > 31) throw new IOException("Invalid SPS identifier.");
        if (extended)
        {
            chroma = bits.Ue(); if (chroma > 3) throw new IOException("Invalid chroma format."); if (chroma == 3) bits.Read(1);
            if (bits.Ue() > 6 || bits.Ue() > 6) throw new IOException("Unsupported bit depth."); bits.Read(1);
            if (bits.Read(1) != 0) for (var i = 0; i < (chroma == 3 ? 12 : 8); i++) if (bits.Read(1) != 0)
            {
                var last = 8; var next = 8;
                for (var j = 0; j < (i < 6 ? 16 : 64); j++)
                {
                    if (next != 0)
                    {
                        var delta = bits.Se(); if (delta is < -128 or > 127) throw new IOException("Invalid scaling-list delta.");
                        next = (last + delta + 256) % 256;
                    }
                    if (next != 0) last = next;
                }
            }
        }
        if (bits.Ue() > 12) throw new IOException("Invalid frame number width."); var order = bits.Ue();
        if (order == 0) { if (bits.Ue() > 12) throw new IOException("Invalid POC width."); }
        else if (order == 1) { bits.Read(1); bits.Se(); bits.Se(); var n = bits.Ue(); if (n > 255) throw new IOException("POC cycle too large."); for (var i = 0; i < n; i++) bits.Se(); }
        else if (order != 2) throw new IOException("Invalid POC type.");
        bits.Ue(); bits.Read(1); bits.Ue(); bits.Ue(); var frameOnly = bits.Read(1); if (frameOnly == 0) bits.Read(1); bits.Read(1);
        if (bits.Read(1) != 0) for (var i = 0; i < 4; i++) bits.Ue();
        var vui = bits.Read(1) != 0; var signal = false; bool? full = null; int? primaries = null, transfer = null, matrix = null;
        if (vui)
        {
            if (bits.Read(1) != 0 && bits.Read(8) == 255) { bits.Read(16); bits.Read(16); }
            if (bits.Read(1) != 0) bits.Read(1);
            signal = bits.Read(1) != 0;
            if (signal) { bits.Read(3); full = bits.Read(1) != 0; if (bits.Read(1) != 0) { primaries = bits.Read(8); transfer = bits.Read(8); matrix = bits.Read(8); } }
        }
        return new(profile, id, vui, signal, full, primaries, transfer, matrix, Convert.ToHexStringLower(SHA256.HashData(nal)));
    }
    sealed class Bits(byte[] bytes)
    {
        int pos;
        public int Read(int count) { if (count is < 1 or > 24 || pos + count > bytes.Length * 8) throw new IOException("Truncated SPS bits."); var value = 0; while (count-- > 0) { value = (value << 1) | ((bytes[pos / 8] >> (7 - pos % 8)) & 1); pos++; } return value; }
        public int Ue() { var zero = 0; while (Read(1) == 0) if (++zero > 20) throw new IOException("Unbounded SPS integer."); return zero == 0 ? 0 : ((1 << zero) - 1) + Read(zero); }
        public int Se() { var n = Ue(); return (n & 1) == 0 ? -(n / 2) : (n + 1) / 2; }
    }
    public static string ReadPinned(string root, string relativePath)
    {
        using var scope = new Scope(root); using var file = scope.Read(relativePath); var before = WindowsPublication.Observe(file, false);
        var prefix = new byte[(int)Math.Min(file.Length, 4 * 1024 * 1024)]; file.ReadExactly(prefix); var tracks = Inspect(prefix);
        if (WindowsPublication.Observe(file, false) != before) throw new IOException("Source changed during inspection.");
        return ExecutionContract.Serialize(new { prefixBytes = prefix.Length, source = before, tracks, mediaOutputFiles = 0, automaticRangeDecision = false });
    }
}
