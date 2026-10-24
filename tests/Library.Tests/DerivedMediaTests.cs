using System.Text.Json;
using System.Text.Json.Nodes;
using Library.Api;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace Library.Tests;

public sealed class DerivedMediaTests
{
    private static JsonElement Probe() => JsonSerializer.SerializeToElement(new {
        format = new { format_name = "matroska,webm", duration = "12" },
        streams = new object[] {
            new { index = 0, codec_type = "video", codec_name = "h264", pix_fmt = "yuv420p", width = 320, height = 180, color_transfer = "bt709", disposition = new { } },
            new { index = 1, codec_type = "audio", codec_name = "dts", tags = new { language = "jpn" }, disposition = new { original = 1 } },
            new { index = 2, codec_type = "audio", codec_name = "aac", tags = new { language = "eng" }, disposition = new { comment = 1 } },
            new { index = 3, codec_type = "audio", codec_name = "ac3", tags = new { language = "eng" }, disposition = new { visual_impaired = 1 } },
            new { index = 4, codec_type = "subtitle", codec_name = "subrip", tags = new { language = "eng" }, disposition = new { forced = 1 } },
            new { index = 5, codec_type = "subtitle", codec_name = "subrip", tags = new { language = "eng" }, disposition = new { hearing_impaired = 1 } },
            new { index = 6, codec_type = "audio", codec_name = "eac3", tags = new { language = "eng" }, disposition = new { } }
        }
    });
    private static SampleRequest Request() => new(1, "staging", 3, ["jpn"], [new(1, "jpn", "main"), new(2, "eng", "commentary"), new(3, "eng", "descriptive"), new(4, "eng", "subtitle"), new(5, "eng", "subtitle"), new(6, "eng", "main")], true, true);
    private static SamplePlan Plan() => new(Request(), 1, new(100, 1, "synthetic"), DerivedMedia.Select(Probe(), Request()), "synthetic", "probe-digest");
    [Fact]
    public void ProbeSignatureIgnoresJsonFormattingButBindsMetadata()
    {
        using var pretty = JsonDocument.Parse(JsonSerializer.Serialize(Probe(), new JsonSerializerOptions { WriteIndented = true }));
        Assert.Equal(DerivedMedia.ProbeSignature(Probe()), DerivedMedia.ProbeSignature(pretty.RootElement));
        var changed = JsonNode.Parse(Probe().GetRawText())!; changed["streams"]![0]!["codec_name"] = "hevc";
        Assert.NotEqual(DerivedMedia.ProbeSignature(Probe()), DerivedMedia.ProbeSignature(JsonSerializer.SerializeToElement(changed)));
    }
    [Fact]
    public void ExplicitPolicyKeepsOriginalEnglishAndAccessibilityWhileDroppingCommentary()
    {
        var selected = DerivedMedia.Select(Probe(), Request());
        Assert.Equal(new[] { 0, 1, 3, 4, 5, 6 }, selected.Select(x => x.Index));
        Assert.Contains("forced", selected[3].Dispositions); Assert.Contains("hearing_impaired", selected[4].Dispositions);
        Assert.Equal("jpn", selected[1].Language);
    }
    [Fact]
    public void MissingReviewUnknownRoleAndUnknownLanguageCannotBecomeAPlan()
    {
        Assert.Throws<ArgumentException>(() => DerivedMedia.Select(Probe(), Request() with { Tracks = [] }));
        Assert.Throws<ArgumentException>(() => DerivedMedia.Select(Probe(), Request() with { Tracks = Request().Tracks.Select(t => t.Index == 1 ? t with { Role = "unknown" } : t).ToArray() }));
        Assert.Throws<ArgumentException>(() => DerivedMedia.Select(Probe(), Request() with { Tracks = Request().Tracks.Select(t => t.Index == 2 ? t with { Language = "und" } : t).ToArray() }));
        Assert.Throws<ArgumentException>(() => DerivedMedia.Select(Probe(), Request() with { OriginalLanguages = [] }));
        Assert.Throws<ArgumentException>(() => DerivedMedia.Select(Probe(), Request() with { ConfirmSourceComplete = false }));
        Assert.Throws<ArgumentException>(() => DerivedMedia.Select(Probe(), Request() with { ConfirmSdr = false }));
        Assert.Throws<ArgumentException>(() => DerivedMedia.Select(Probe(), Request() with { Seconds = 31 }));
    }
    [Theory]
    [InlineData("color_transfer", "smpte2084")]
    [InlineData("color_transfer", "arib-std-b67")]
    [InlineData("codec_name", "hevc")]
    [InlineData("pix_fmt", "yuv420p10le")]
    public void HdrAndUnqualifiedVideoRemainBlockedDespiteUserConfirmation(string field, string value)
    {
        var node = JsonNode.Parse(Probe().GetRawText())!; node["streams"]![0]![field] = value;
        Assert.Throws<ArgumentException>(() => DerivedMedia.Select(JsonSerializer.SerializeToElement(node), Request()));
        node = JsonNode.Parse(Probe().GetRawText())!; node["streams"]![0]!["side_data_list"] = JsonNode.Parse("[{\"side_data_type\":\"DOVI configuration record\",\"dv_profile\":8}]");
        Assert.Throws<ArgumentException>(() => DerivedMedia.Select(JsonSerializer.SerializeToElement(node), Request()));
    }
    [Fact]
    public void CommandUsesOnlyExplicitCopyMapsAndPinnedOutput()
    {
        var arguments = DerivedMedia.Arguments("/proc/1/fd/20", "/proc/1/fd/21", Plan());
        Assert.Equal("copy", arguments[Array.IndexOf(arguments, "-c") + 1]);
        Assert.Equal(new[] { "0:0", "0:1", "0:3", "0:4", "0:5", "0:6" }, arguments.Select((v, i) => (v, i)).Where(x => x.v == "-map").Select(x => arguments[x.i + 1]));
        Assert.DoesNotContain("-vf", arguments); Assert.DoesNotContain("-af", arguments); Assert.DoesNotContain("-crf", arguments); Assert.DoesNotContain("-preset", arguments);
        Assert.Equal("/proc/1/fd/21", arguments[^1]); Assert.Contains("-fs", arguments);
    }
    private static JsonElement Packet(int stream, string hash, double pts) => JsonSerializer.SerializeToElement(new { stream_index = stream, data_hash = "SHA256:" + hash, pts_time = pts.ToString(System.Globalization.CultureInfo.InvariantCulture), size = "12" });
    [Fact]
    public void PacketProofRejectsAlteredPayloadMissingAudioAndSynchronizationDrift()
    {
        var plan = Plan() with { Streams = [new(0, "video", "h264", "", []), new(1, "audio", "dts", "jpn", [])] };
        JsonElement[] packets = [Packet(0, "video-one", 0), Packet(0, "video-two", 1), Packet(1, "audio-one", 0), Packet(1, "audio-two", 1)];
        SampleRemuxService.ValidatePackets(plan, packets, packets);
        var altered = packets.ToArray(); altered[2] = Packet(1, "encoded-audio", 0);
        Assert.Throws<IOException>(() => SampleRemuxService.ValidatePackets(plan, packets, altered));
        Assert.Throws<IOException>(() => SampleRemuxService.ValidatePackets(plan, packets, packets[..3]));
        altered = packets.ToArray(); altered[3] = Packet(1, "audio-two", 1.1);
        Assert.Throws<IOException>(() => SampleRemuxService.ValidatePackets(plan, packets, altered));
    }
    [Fact]
    public void CopiedSubtitlesMustRemainSynchronized()
    {
        var plan = Plan() with { Streams = [new(0, "video", "h264", "", []), new(1, "audio", "dts", "jpn", []), new(2, "subtitle", "subrip", "eng", [])] };
        JsonElement[] source = [Packet(0, "video", 0), Packet(1, "audio", 0), Packet(2, "subtitle", 1)];
        SampleRemuxService.ValidatePackets(plan, source, source);
        JsonElement[] shifted = [source[0], source[1], Packet(2, "subtitle", 1.5)];
        Assert.Throws<IOException>(() => SampleRemuxService.ValidatePackets(plan, source, shifted));
    }
    [Fact]
    public void SchemaMigrationIsAdditiveAndDerivedLineageIsImmutable()
    {
        var home = Path.Combine(Path.GetTempPath(), "library-derived-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(home);
        try
        {
            using var db = new Catalog(new DbContextOptionsBuilder<Catalog>().UseSqlite("Data Source=" + Path.Combine(home, "catalog.db")).Options);
            CatalogSchema.Initialize(db);
            db.Database.ExecuteSqlRaw("DROP TABLE DerivedAssets; DROP TABLE ProcessingLeases; PRAGMA user_version = 5;");
            db.History.Add(new() { Action = "before-migration", Detail = "retain" }); db.SaveChanges();
            CatalogSchema.Initialize(db); CatalogSchema.Initialize(db);
            Assert.Single(db.History.Where(h => h.Action == "before-migration"));
            var title = new Title(); db.Titles.Add(title); db.SaveChanges(); var edition = new Edition { TitleId = title.Id }; db.Editions.Add(edition); db.SaveChanges();
            var source = new Asset { EditionId = edition.Id, Sha256 = "original-hash" }; var output = new Asset { EditionId = edition.Id, Sha256 = "derived-hash" }; db.Assets.AddRange(source, output); db.SaveChanges();
            var copy = new PhysicalCopy { AssetId = source.Id, RootId = "source", RelativePath = "invented.mkv" }; db.Copies.Add(copy); db.SaveChanges();
            db.DerivedAssets.Add(new() { OutputAssetId = output.Id, SourceAssetId = source.Id, SourceCopyId = copy.Id, JobId = "synthetic" }); db.SaveChanges();
            Assert.NotEqual(source.Id, output.Id); Assert.Equal("original-hash", db.Assets.Single(a => a.Id == source.Id).Sha256);
            Assert.Throws<Microsoft.Data.Sqlite.SqliteException>(() => db.Database.ExecuteSqlRaw("UPDATE DerivedAssets SET Kind = 'original';"));
            Assert.Throws<Microsoft.Data.Sqlite.SqliteException>(() => db.Database.ExecuteSqlRaw("DELETE FROM DerivedAssets;"));
        }
        finally { Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools(); Directory.Delete(home, true); }
    }
}
