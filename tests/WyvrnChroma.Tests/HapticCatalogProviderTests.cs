using System.IO.Compression;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using Xunit;

namespace WyvrnChroma.Tests;

public sealed class HapticCatalogProviderTests : IDisposable
{
    private const string ManifestUrl = "https://cdn.test/hapticfolders/manifest.json";
    private const string ZipUrl = "https://cdn.test/hapticfolders/hapticFolders-v1.zip";

    private readonly List<string> _tempDirs = new();

    private string NewTempDir()
    {
        var d = Path.Combine(Path.GetTempPath(), "wyvrn-test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(d);
        _tempDirs.Add(d);
        return d;
    }

    public void Dispose()
    {
        foreach (var d in _tempDirs)
            try { if (Directory.Exists(d)) Directory.Delete(d, recursive: true); } catch { /* best effort */ }
    }

    /// <summary>An HttpMessageHandler that serves canned per-URL responses and records every request.</summary>
    private sealed class StubHandler : HttpMessageHandler
    {
        public readonly Dictionary<string, (HttpStatusCode status, byte[] body)> Responses = new();
        public readonly List<string> Requests = new();

        public bool Offline;

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            var url = request.RequestUri!.ToString();
            Requests.Add(url);
            if (Offline)
                return Task.FromException<HttpResponseMessage>(new HttpRequestException("simulated offline"));
            return Task.FromResult(Responses.TryGetValue(url, out var r)
                ? new HttpResponseMessage(r.status) { Content = new ByteArrayContent(r.body) }
                : new HttpResponseMessage(HttpStatusCode.NotFound));
        }
    }

    private static byte[] MakeZip(params (string path, byte[] content)[] entries)
    {
        using var ms = new MemoryStream();
        using (var zip = new ZipArchive(ms, ZipArchiveMode.Create, leaveOpen: true))
        {
            foreach (var (path, content) in entries)
            {
                var entry = zip.CreateEntry(path);
                using var s = entry.Open();
                s.Write(content, 0, content.Length);
            }
        }
        return ms.ToArray();
    }

    private static byte[] Utf8(string s) => Encoding.UTF8.GetBytes(s);
    private static string Sha(byte[] b) => Convert.ToHexString(SHA256.HashData(b));

    [Fact]
    public async Task LocalInstall_Present_UsedWithoutAnyNetwork()
    {
        var local = NewTempDir();
        Directory.CreateDirectory(Path.Combine(local, "007 First Light")); // a game folder => a real install

        var handler = new StubHandler();
        var provider = new HapticCatalogProvider(new HttpClient(handler),
            localInstallCandidates: new[] { local }, manifestUrl: ManifestUrl, dataDirectory: NewTempDir());

        var result = await provider.EnsureCatalogAsync();

        Assert.Equal(CatalogSource.LocalInstall, result.Source);
        Assert.Equal(local, result.RootDirectory);
        Assert.Empty(handler.Requests); // local-first: nothing fetched
    }

    [Fact]
    public async Task NoLocal_DownloadsVerifiesAndExtracts()
    {
        var zip = MakeZip(
            ("007 First Light/wyvrn.config", Utf8("{\"ExternalCommands\":[]}")),
            ("007 First Light/Idle_Keyboard.chroma", new byte[] { 1, 2, 3, 4 }));
        var manifest = Utf8($"{{\"version\":\"1.2.3\",\"file\":\"hapticFolders-v1.zip\",\"sha256\":\"{Sha(zip)}\"}}");

        var handler = new StubHandler();
        handler.Responses[ManifestUrl] = (HttpStatusCode.OK, manifest);
        handler.Responses[ZipUrl] = (HttpStatusCode.OK, zip);

        var provider = new HapticCatalogProvider(new HttpClient(handler),
            localInstallCandidates: new[] { @"Z:\does\not\exist" }, manifestUrl: ManifestUrl, dataDirectory: NewTempDir());

        var result = await provider.EnsureCatalogAsync();

        Assert.Equal(CatalogSource.Cdn, result.Source);
        Assert.Equal("1.2.3", result.Version);
        Assert.True(File.Exists(Path.Combine(result.RootDirectory, "007 First Light", "wyvrn.config")));
        Assert.True(File.Exists(Path.Combine(result.RootDirectory, "007 First Light", "Idle_Keyboard.chroma")));
    }

    [Fact]
    public async Task Sha256Mismatch_Throws()
    {
        var zip = MakeZip(("g/x.chroma", new byte[] { 9 }));
        var manifest = Utf8("{\"version\":\"1\",\"file\":\"hapticFolders-v1.zip\",\"sha256\":\"00DEADBEEF\"}");
        var handler = new StubHandler();
        handler.Responses[ManifestUrl] = (HttpStatusCode.OK, manifest);
        handler.Responses[ZipUrl] = (HttpStatusCode.OK, zip);

        var provider = new HapticCatalogProvider(new HttpClient(handler),
            localInstallCandidates: new[] { @"Z:\nope" }, manifestUrl: ManifestUrl, dataDirectory: NewTempDir());

        await Assert.ThrowsAsync<CatalogIntegrityException>(() => provider.EnsureCatalogAsync());
    }

    [Fact]
    public async Task CachedVersion_Reused_WithoutDownloadingZip()
    {
        var manifest = Utf8("{\"version\":\"7.7\",\"file\":\"hapticFolders-v1.zip\",\"sha256\":\"abc\"}");
        var handler = new StubHandler();
        handler.Responses[ManifestUrl] = (HttpStatusCode.OK, manifest); // no ZIP response on purpose

        var dataDir = NewTempDir();
        var cached = Path.Combine(dataDir, "7.7", "007 First Light");
        Directory.CreateDirectory(cached);
        File.WriteAllText(Path.Combine(cached, "wyvrn.config"), "{}");

        var provider = new HapticCatalogProvider(new HttpClient(handler),
            localInstallCandidates: new[] { @"Z:\nope" }, manifestUrl: ManifestUrl, dataDirectory: dataDir);

        var result = await provider.EnsureCatalogAsync();

        Assert.Equal(CatalogSource.Cache, result.Source);
        Assert.Equal("7.7", result.Version);
        Assert.DoesNotContain(ZipUrl, handler.Requests); // only the manifest was requested
    }

    [Fact]
    public async Task Manifest_MissingFields_Throws()
    {
        var handler = new StubHandler();
        handler.Responses[ManifestUrl] = (HttpStatusCode.OK, Utf8("{\"version\":\"1\"}")); // missing file/sha256

        var provider = new HapticCatalogProvider(new HttpClient(handler),
            localInstallCandidates: new[] { @"Z:\nope" }, manifestUrl: ManifestUrl, dataDirectory: NewTempDir());

        await Assert.ThrowsAsync<CatalogIntegrityException>(() => provider.EnsureCatalogAsync());
    }

    [Fact]
    public async Task Offline_WithCachedVersion_FallsBackToCache()
    {
        var dataDir = NewTempDir();
        var cached = Path.Combine(dataDir, "5.0", "007 First Light");
        Directory.CreateDirectory(cached);
        File.WriteAllText(Path.Combine(cached, "wyvrn.config"), "{}");

        var handler = new StubHandler { Offline = true }; // CDN unreachable
        var provider = new HapticCatalogProvider(new HttpClient(handler),
            localInstallCandidates: new[] { @"Z:\nope" }, manifestUrl: ManifestUrl, dataDirectory: dataDir);

        var result = await provider.EnsureCatalogAsync();

        Assert.Equal(CatalogSource.Cache, result.Source);
        Assert.Equal("5.0", result.Version); // used the already-downloaded copy despite being offline
    }

    [Fact]
    public async Task Offline_NoCache_ThrowsUnavailable()
    {
        var handler = new StubHandler { Offline = true };
        var provider = new HapticCatalogProvider(new HttpClient(handler),
            localInstallCandidates: new[] { @"Z:\nope" }, manifestUrl: ManifestUrl, dataDirectory: NewTempDir());

        await Assert.ThrowsAsync<CatalogUnavailableException>(() => provider.EnsureCatalogAsync());
    }

    [Fact]
    public async Task CheckForUpdatesFalse_WithCache_DoesNotTouchNetwork()
    {
        var dataDir = NewTempDir();
        var cached = Path.Combine(dataDir, "9.9", "007 First Light");
        Directory.CreateDirectory(cached);
        File.WriteAllText(Path.Combine(cached, "wyvrn.config"), "{}");

        var handler = new StubHandler();
        var provider = new HapticCatalogProvider(new HttpClient(handler),
            localInstallCandidates: new[] { @"Z:\nope" }, manifestUrl: ManifestUrl, dataDirectory: dataDir);

        var result = await provider.EnsureCatalogAsync(checkForUpdates: false);

        Assert.Equal(CatalogSource.Cache, result.Source);
        Assert.Equal("9.9", result.Version);
        Assert.Empty(handler.Requests); // fully offline path
    }

    [Fact]
    public async Task RealZipLayout_HapticFoldersWrapper_AndBackslashes_ExtractedAndRooted()
    {
        // The real CDN ZIP wraps the per-game folders in "hapticFolders\" and uses Windows '\' separators.
        var zip = MakeZip(
            (@"hapticFolders\007 First Light\wyvrn.config", Utf8("{}")),
            (@"hapticFolders\007 First Light\Idle_Keyboard.chroma", new byte[] { 1, 2, 3, 4 }));
        var manifest = Utf8($"{{\"version\":\"0.0.1.76\",\"file\":\"hapticFolders-v1.zip\",\"sha256\":\"{Sha(zip)}\"}}");

        var handler = new StubHandler();
        handler.Responses[ManifestUrl] = (HttpStatusCode.OK, manifest);
        handler.Responses[ZipUrl] = (HttpStatusCode.OK, zip);

        var provider = new HapticCatalogProvider(new HttpClient(handler),
            localInstallCandidates: new[] { @"Z:\nope" }, manifestUrl: ManifestUrl, dataDirectory: NewTempDir());

        var result = await provider.EnsureCatalogAsync();

        Assert.Equal(CatalogSource.Cdn, result.Source);
        // RootDirectory points at the per-game-folders dir (the "hapticFolders" subdir), matching the local layout.
        Assert.Equal("hapticFolders", Path.GetFileName(result.RootDirectory));
        Assert.True(File.Exists(Path.Combine(result.RootDirectory, "007 First Light", "Idle_Keyboard.chroma")));
        Assert.True(File.Exists(Path.Combine(result.RootDirectory, "007 First Light", "wyvrn.config")));
    }
}
