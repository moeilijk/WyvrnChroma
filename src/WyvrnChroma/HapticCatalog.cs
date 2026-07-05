using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace WyvrnChroma;

/// <summary>Where the resolved Wyvrn haptic-folder catalog came from.</summary>
public enum CatalogSource
{
    /// <summary>An existing local Synapse/Chroma/Wyvrn install (no download).</summary>
    LocalInstall,

    /// <summary>A previously downloaded copy of the current version on this machine.</summary>
    Cache,

    /// <summary>Freshly downloaded from Razer's public CDN.</summary>
    Cdn,
}

/// <summary>The catalog manifest served at <c>apps.razer.com/hapticfolders/manifest.json</c>.</summary>
public sealed record HapticCatalogManifest(
    [property: JsonPropertyName("version")] string Version,
    [property: JsonPropertyName("file")] string File,
    [property: JsonPropertyName("sha256")] string Sha256);

/// <summary>The resolved catalog: a directory that contains the per-game folders.</summary>
public sealed record CatalogResult(string RootDirectory, CatalogSource Source, string? Version);

/// <summary>
/// Resolves the Wyvrn haptic-folder catalog (the per-game <c>wyvrn.config</c> + <c>.chroma</c> files) with this
/// priority, per the #292 plan:
/// <list type="number">
///   <item>An existing LOCAL Synapse/Chroma/Wyvrn install — used directly, no download.</item>
///   <item>Otherwise the public Razer CDN — manifest → versioned ZIP → sha256-verified → extracted + cached.</item>
/// </list>
/// Nothing is bundled or redistributed; the data is fetched on the user's own machine from Razer's own CDN.
/// </summary>
public sealed class HapticCatalogProvider
{
    public const string DefaultManifestUrl = "https://apps.razer.com/hapticfolders/manifest.json";

    public static readonly string[] DefaultLocalInstallCandidates =
    {
        @"C:\Program Files (x86)\Interhaptics\hapticFolders",
        @"C:\Program Files\Interhaptics\hapticFolders",
    };

    public static string DefaultDataDirectory => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "WyvrnChroma", "hapticFolders");

    private readonly HttpClient _http;
    private readonly IReadOnlyList<string> _localCandidates;
    private readonly string _manifestUrl;
    private readonly string _dataDir;

    public HapticCatalogProvider(
        HttpClient http,
        IReadOnlyList<string>? localInstallCandidates = null,
        string? manifestUrl = null,
        string? dataDirectory = null)
    {
        _http = http ?? throw new ArgumentNullException(nameof(http));
        _localCandidates = localInstallCandidates ?? DefaultLocalInstallCandidates;
        _manifestUrl = manifestUrl ?? DefaultManifestUrl;
        _dataDir = dataDirectory ?? DefaultDataDirectory;
    }

    /// <summary>
    /// Resolve the catalog directory. The (large) catalog is downloaded from the CDN <b>once</b> and then reused
    /// <b>offline</b>: on later runs a cheap <c>manifest.json</c> check decides whether a newer version must be
    /// fetched, but when the CDN is unreachable an already-downloaded copy is used. Network is only mandatory on
    /// the very first run; runtime after that needs no cloud.
    /// </summary>
    /// <param name="checkForUpdates">
    /// When <c>false</c>, an existing local/cached catalog is returned <b>without contacting the CDN at all</b>.
    /// </param>
    public async Task<CatalogResult> EnsureCatalogAsync(bool checkForUpdates = true, CancellationToken ct = default)
    {
        // 1. An existing local Synapse/Chroma/Wyvrn install is used as-is (a bonus, not the standalone path).
        foreach (var dir in _localCandidates)
            if (!string.IsNullOrWhiteSpace(dir) && Directory.Exists(dir) && Directory.EnumerateDirectories(dir).Any())
                return new CatalogResult(dir, CatalogSource.LocalInstall, null);

        // 2. Standalone path: a versioned copy downloaded from the CDN, cached under the data dir.
        var cached = NewestCachedVersion();

        // Offline by choice (or simply already have a copy and the caller does not want an update check).
        if (!checkForUpdates && cached is not null)
            return new CatalogResult(ResolveGameRoot(cached.Value.dir), CatalogSource.Cache, cached.Value.version);

        try
        {
            var manifest = await GetManifestAsync(ct).ConfigureAwait(false);
            var target = Path.Combine(_dataDir, manifest.Version);

            if (HasContent(target)) // this version is already downloaded
                return new CatalogResult(ResolveGameRoot(target), CatalogSource.Cache, manifest.Version);

            // A newer version (or nothing cached yet) — download → verify → extract.
            var zip = await _http.GetByteArrayAsync(CombineUrl(_manifestUrl, manifest.File), ct).ConfigureAwait(false);
            VerifySha256(zip, manifest.Sha256); // integrity errors deliberately propagate (not hidden by the offline fallback)
            ExtractZip(zip, target);
            return new CatalogResult(ResolveGameRoot(target), CatalogSource.Cdn, manifest.Version);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or IOException)
        {
            // CDN unreachable / offline: fall back to whatever was downloaded earlier; only fail when nothing exists.
            if (cached is not null)
                return new CatalogResult(ResolveGameRoot(cached.Value.dir), CatalogSource.Cache, cached.Value.version);
            throw new CatalogUnavailableException(
                "No catalog available: no local install, nothing cached, and the Razer CDN could not be reached.", ex);
        }
    }

    /// <summary>The most recently downloaded catalog version on disk, or <c>null</c> when nothing is cached.</summary>
    private (string dir, string version)? NewestCachedVersion()
    {
        if (!Directory.Exists(_dataDir))
            return null;

        var newest = Directory.EnumerateDirectories(_dataDir)
            .Where(HasContent)
            .OrderByDescending(Directory.GetLastWriteTimeUtc)
            .FirstOrDefault();

        return newest is null ? null : (newest, Path.GetFileName(newest));
    }

    private static bool HasContent(string dir) =>
        Directory.Exists(dir) && Directory.EnumerateFileSystemEntries(dir).Any();

    /// <summary>
    /// The CDN ZIP wraps the per-game folders in a <c>hapticFolders</c> directory; return the directory that
    /// directly contains the per-game folders so the result matches the local-install layout.
    /// </summary>
    private static string ResolveGameRoot(string extractedVersionDir)
    {
        var wrapped = Path.Combine(extractedVersionDir, "hapticFolders");
        return Directory.Exists(wrapped) ? wrapped : extractedVersionDir;
    }

    public async Task<HapticCatalogManifest> GetManifestAsync(CancellationToken ct = default)
    {
        await using var stream = await _http.GetStreamAsync(_manifestUrl, ct).ConfigureAwait(false);
        var manifest = await JsonSerializer.DeserializeAsync<HapticCatalogManifest>(stream, cancellationToken: ct)
            .ConfigureAwait(false);
        if (manifest is null || string.IsNullOrEmpty(manifest.File) || string.IsNullOrEmpty(manifest.Sha256))
            throw new CatalogIntegrityException("Catalog manifest is empty or missing fields.");
        return manifest;
    }

    private static void VerifySha256(byte[] data, string expectedHex)
    {
        var actual = Convert.ToHexString(SHA256.HashData(data));
        if (!string.Equals(actual, expectedHex, StringComparison.OrdinalIgnoreCase))
            throw new CatalogIntegrityException($"Catalog sha256 mismatch: expected {expectedHex}, got {actual}.");
    }

    private static void ExtractZip(byte[] zipBytes, string target)
    {
        Directory.CreateDirectory(target);
        var root = Path.GetFullPath(target) + Path.DirectorySeparatorChar;

        using var ms = new MemoryStream(zipBytes, writable: false);
        using var zip = new ZipArchive(ms, ZipArchiveMode.Read);
        foreach (var entry in zip.Entries)
        {
            if (entry.FullName.EndsWith('/') || entry.FullName.EndsWith('\\'))
                continue; // directory entry

            // The catalog ZIP uses Windows '\' separators; normalise so extraction is correct cross-platform.
            var relative = entry.FullName.Replace('\\', Path.DirectorySeparatorChar).Replace('/', Path.DirectorySeparatorChar);
            var dest = Path.GetFullPath(Path.Combine(target, relative));
            if (!dest.StartsWith(root, StringComparison.Ordinal))
                throw new CatalogIntegrityException($"Zip entry escapes target directory: {entry.FullName}");

            Directory.CreateDirectory(Path.GetDirectoryName(dest)!);
            using var entryStream = entry.Open();
            using var file = File.Create(dest);
            entryStream.CopyTo(file);
        }
    }

    private static string CombineUrl(string manifestUrl, string file)
    {
        var slash = manifestUrl.LastIndexOf('/');
        return slash < 0 ? file : manifestUrl[..(slash + 1)] + file;
    }
}

/// <summary>Thrown when the catalog manifest is invalid or the downloaded ZIP fails its sha256 check.</summary>
public sealed class CatalogIntegrityException : Exception
{
    public CatalogIntegrityException(string message) : base(message)
    {
    }
}

/// <summary>Thrown when no catalog can be resolved: no local install, nothing cached, and the CDN is unreachable.</summary>
public sealed class CatalogUnavailableException : Exception
{
    public CatalogUnavailableException(string message, Exception innerException) : base(message, innerException)
    {
    }
}
