using System.Net;
using Library.Api;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace Library.Tests;

public sealed class QBitContractTests
{
    private static QBitConfiguration Config => new("synthetic", "https://synthetic.invalid/", "Movies & TV", [new("X:/Downloads", "source")]);
    private static string Torrents(string state = "downloading") => "[{\"hash\":\"aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa\",\"save_path\":\"X:/Downloads\",\"state\":\"" + state + "\",\"progress\":0.25,\"completion_on\":-1,\"seeding_time\":0}]";
    private const string Files = "[{\"index\":0,\"name\":\"Series/episode01.mkv\",\"size\":100,\"progress\":1},{\"index\":1,\"name\":\"Series/episode02.mkv\",\"size\":200,\"progress\":0.4}]";
    [Theory]
    [InlineData("v4.6.7", "2.8.19", "pausedUP")]
    [InlineData("v5.0.4", "2.11.0", "stoppedUP")]
    [InlineData("v5.0.4", "2.11.0", "downloading")]
    public async Task VersionsUsePerFileProgressAndOnlyReadTorrentEndpoints(string application, string api, string state)
    {
        var handler = new ContractHandler(application, api, Torrents(state), Files);
        using var client = new HttpClient(handler) { BaseAddress = new("https://synthetic.invalid/") };
        var snapshot = await new QBitReadOnlyAdapter(client).ReadAsync(Config, "synthetic-user", "synthetic-password");
        Assert.Equal("complete", snapshot.Files[0].Readiness); Assert.Equal("incomplete", snapshot.Files[1].Readiness);
        Assert.Equal("source", snapshot.Files[0].RootId); Assert.Equal("Series/episode01.mkv", snapshot.Files[0].RelativePath);
        Assert.Null(snapshot.Files[0].CompletedAtUnix); Assert.Equal(0, snapshot.Files[0].SeedingSeconds);
        Assert.All(handler.Requests.Skip(1), x => Assert.Equal(HttpMethod.Get, x.Method));
        Assert.Equal("/api/v2/auth/login", handler.Requests[0].Path); Assert.Equal(HttpMethod.Post, handler.Requests[0].Method);
        Assert.Equal(5, handler.Requests.Count); Assert.Contains("category=Movies%20%26%20TV", handler.Requests[3].Query);
    }
    [Theory]
    [InlineData("checkingUP")]
    [InlineData("moving")]
    [InlineData("missingFiles")]
    [InlineData("futureUnknownState")]
    public async Task UnsafeAndUnknownTorrentStatesBlockCompleteFiles(string state)
    {
        using var client = new HttpClient(new ContractHandler("v5.0.4", "2.11.0", Torrents(state), Files)) { BaseAddress = new("https://synthetic.invalid/") };
        var snapshot = await new QBitReadOnlyAdapter(client).ReadAsync(Config, "synthetic", "synthetic"); Assert.All(snapshot.Files, f => Assert.Equal("blocked", f.Readiness));
    }
    [Theory]
    [InlineData("v4.4.0", "2.8.2")]
    [InlineData("v5.0.0", "2.7.0")]
    [InlineData("v6.0.0", "3.0")]
    [InlineData("invalid", "2.11.0")]
    public async Task UnsupportedVersionsStopBeforeTorrentReads(string version, string api)
    {
        var handler = new ContractHandler(version, api, Torrents(), Files);
        using var client = new HttpClient(handler) { BaseAddress = new("https://synthetic.invalid/") };
        await Assert.ThrowsAsync<InvalidOperationException>(() => new QBitReadOnlyAdapter(client).ReadAsync(Config, "synthetic", "synthetic"));
        Assert.Equal(3, handler.Requests.Count);
    }
    [Fact]
    public async Task AuthenticationFailureDoesNotRetryOrReadTorrents()
    {
        var handler = new ContractHandler("v5.0.4", "2.11.0", Torrents(), Files) { LoginBody = "Fails." };
        using var client = new HttpClient(handler) { BaseAddress = new("https://synthetic.invalid/") };
        await Assert.ThrowsAsync<InvalidOperationException>(() => new QBitReadOnlyAdapter(client).ReadAsync(Config, "synthetic", "synthetic")); Assert.Single(handler.Requests);
    }
    [Fact]
    public async Task MissingFileIndexDoesNotProduceACompleteSnapshot()
    {
        using var client = new HttpClient(new ContractHandler("v5.0.4", "2.11.0", Torrents(), "[{\"name\":\"movie.mkv\",\"size\":1,\"progress\":1}]")) { BaseAddress = new("https://synthetic.invalid/") };
        await Assert.ThrowsAsync<KeyNotFoundException>(() => new QBitReadOnlyAdapter(client).ReadAsync(Config, "synthetic", "synthetic"));
    }
    [Fact]
    public void MappingUsesBoundaryAndLongestPrefixWithoutTraversal()
    {
        QBitPathMapping[] mappings = [new("X:/Downloads", "source"), new("X:/Downloads/TV", "tv-source", "incoming")];
        Assert.Equal(("tv-source", "incoming/Season 01/E01.mkv", null), QBitPaths.Map("x:\\downloads\\TV", "Season 01\\E01.mkv", mappings));
        Assert.Null(QBitPaths.Map("X:/Downloads-other", "movie.mkv", mappings).Root);
        Assert.NotNull(QBitPaths.Map("X:/Downloads", "../movie.mkv", mappings).Error);
        Assert.NotNull(QBitPaths.Map("X:/Downloads", "/absolute.mkv", mappings).Error);
        Assert.Equal(("linux", "series/e01.mkv", null), QBitPaths.Map("/downloads", "series/e01.mkv", [new("/downloads", "linux")]));
    }
    [Fact]
    public void AmbiguousMappingFailsClosed()
    {
        var mapped = QBitPaths.Map("X:/Downloads", "movie.mkv", [new("X:/Downloads", "one"), new("x:/downloads", "two")]); Assert.Null(mapped.Root); Assert.Contains("Ambiguous", mapped.Error);
    }
}
public sealed class ContractHandler(string app, string api, string torrents, string files) : HttpMessageHandler
{
    public string LoginBody { get; init; } = "Ok.";
    public List<(HttpMethod Method, string Path, string Query)> Requests { get; } = [];
    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
    {
        Requests.Add((request.Method, request.RequestUri!.AbsolutePath, request.RequestUri.Query));
        var body = request.RequestUri.AbsolutePath switch { "/api/v2/auth/login" => LoginBody, "/api/v2/app/version" => app, "/api/v2/app/webapiVersion" => api, "/api/v2/torrents/info" => torrents, "/api/v2/torrents/files" => files, _ => throw new Exception("Noncontract endpoint requested.") };
        return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(body) });
    }
}
public sealed class QBitWorkflowTests : IDisposable
{
    private readonly string home = Path.Combine(Path.GetTempPath(), "library-integrations-" + Guid.NewGuid().ToString("N"));
    private readonly TestFactory factory;
    private readonly QBitIntegration integration;
    public QBitWorkflowTests()
    {
        Directory.CreateDirectory(Path.Combine(home, "source")); Directory.CreateDirectory(Path.Combine(home, "data"));
        factory = new(new DbContextOptionsBuilder<Catalog>().UseSqlite("Data Source=" + Path.Combine(home, "data", "catalog.db")).Options);
        using var db = factory.CreateDbContext(); CatalogSchema.Initialize(db);
        var storage = new Storage(new LibraryOptions { DataPath = Path.Combine(home, "data"), Roots = [new("source", "source", Path.Combine(home, "source"), "", "source")] });
        integration = new(factory, storage);
    }
    [Fact]
    public async Task DisabledModeLeavesManualWorkflowUsable()
    {
        var evidence = await integration.CheckAsync(new() { RootId = "source", RelativePath = "manual.mkv" }, null, CancellationToken.None); Assert.Equal("manual", evidence.State);
        Assert.Equal("disabled", (await integration.Refresh(CancellationToken.None)).State);
    }
    [Fact]
    public async Task SyntheticCompletionBlocksPartialFileAndChecksPhysicalSize()
    {
        await integration.Save(new("synthetic", "", "", [new("X:/LibraryFixture/source", "source")]), CancellationToken.None);
        var complete = new PhysicalCopy { RootId = "source", RelativePath = "Northlight.2024.1080p.mkv", Role = "source" };
        await File.WriteAllBytesAsync(Path.Combine(home, "source", complete.RelativePath), new byte[384000]);
        var evidence = await integration.CheckAsync(complete, 1, CancellationToken.None); Assert.Equal("complete", evidence.State); Assert.True(evidence.Synthetic);
        await Assert.ThrowsAsync<IOException>(() => integration.CheckAsync(new() { RootId = "source", RelativePath = "Paper.Moons.2021.mkv" }, 1, CancellationToken.None));
        await File.WriteAllTextAsync(Path.Combine(home, "source", complete.RelativePath), "wrong size");
        await Assert.ThrowsAsync<IOException>(() => integration.CheckAsync(complete, 1, CancellationToken.None));
    }
    [Fact]
    public async Task ConfigurationRevisionCannotBypassApprovedReadinessCheck()
    {
        await integration.Save(new("synthetic", "", "", [new("X:/LibraryFixture/source", "source")]), CancellationToken.None);
        await integration.Save(new("disabled", "", "", [], 1), CancellationToken.None);
        await Assert.ThrowsAsync<IOException>(() => integration.CheckAsync(new() { RootId = "source", RelativePath = "movie.mkv" }, 1, CancellationToken.None));
        await Assert.ThrowsAsync<ArgumentException>(() => integration.Save(new("disabled", "", "", [], 0), CancellationToken.None));
    }
    [Fact]
    public async Task UnmatchedFileRemainsManualAndConfigurationContainsNoCredentials()
    {
        await integration.Save(new("synthetic", "", "", [new("X:/LibraryFixture/source", "source")]), CancellationToken.None);
        var result = await integration.CheckAsync(new() { RootId = "source", RelativePath = "unmatched.mkv" }, 1, CancellationToken.None); Assert.Equal("manual", result.State);
        await using var db = factory.CreateDbContext(); var payload = (await db.Integrations.SingleAsync()).Payload; Assert.DoesNotContain("password", payload, StringComparison.OrdinalIgnoreCase); Assert.DoesNotContain("username", payload, StringComparison.OrdinalIgnoreCase);
    }
    public void Dispose() { Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools(); Directory.Delete(home, true); }
}
public sealed class MetadataProviderTests
{
    private readonly MetadataProviders providers = new([new SyntheticMetadataProvider(), new UnconfiguredMetadataProvider()]);
    [Fact]
    public async Task SyntheticSearchReturnsComparableCandidatesAndNoMatchIsUsable()
    {
        var provider = providers.Get("synthetic"); var results = await provider.SearchAsync("Northlight", "movie", CancellationToken.None);
        Assert.Equal(2, results.Count); Assert.All(results, result => Assert.True(result.Synthetic));
        Assert.Empty(await provider.SearchAsync("unmatched manual title", "movie", CancellationToken.None));
    }
    [Fact]
    public async Task MatchIsOnlyAReviewPlanAndPreservesManualLocks()
    {
        var title = new Title { Id = 1, Name = "My title", Year = 2020, LockedFields = "[\"name\"]", Revision = 3 };
        var plan = await providers.Match(title, new(1, 3, "synthetic", "northlight-2024"), CancellationToken.None);
        Assert.Equal("My title", title.Name); Assert.Equal(2020, title.Year); Assert.Equal("leave", plan.Changes[0].Fields["name"].Mode);
        Assert.Equal("set", plan.Changes[0].Fields["year"].Mode); Assert.True(plan.Provenance!.Synthetic);
        var preview = Metadata.Apply(title, plan.Changes[0]); Assert.Equal("My title", preview.Name); Assert.Equal(2024, preview.Year);
    }
    [Fact]
    public async Task WrongKindAndStaleRevisionCannotBeMatched()
    {
        var title = new Title { Id = 1, Kind = "tv", Revision = 2 };
        await Assert.ThrowsAsync<ArgumentException>(() => providers.Match(title, new(1, 2, "synthetic", "northlight-2024"), CancellationToken.None));
        await Assert.ThrowsAsync<ArgumentException>(() => providers.Match(title, new(1, 1, "synthetic", "signal-coast-2023"), CancellationToken.None));
    }
    [Fact]
    public async Task LiveProviderIsExplicitlyUnconfiguredAndReturnsNoInventedResults()
    {
        var provider = providers.Get("tmdb"); Assert.Equal("unconfigured", provider.Status.State); Assert.True(provider.Status.RequiresApiKey);
        await Assert.ThrowsAsync<InvalidOperationException>(() => provider.SearchAsync("Northlight", "movie", CancellationToken.None));
    }
}
