using System.Security.Cryptography;
using System.Text;
using Winference.Core.Downloads;
using Winference.Core.Hybrid;
using Winference.Core.Runtime;
using Winference.Core.Setup;
using Winference.Infrastructure.Hybrid;
using Winference.Infrastructure.Setup;

namespace Winference.Infrastructure.Discover;

/// <summary>
/// Downloads an AMD Ryzen AI GPU + NPU export from Hugging Face into an app-owned folder. Every
/// file is checked against the fingerprint Hugging Face publishes for the pinned commit, and only
/// then is the folder registered through the same checks as a manual import.
/// </summary>
public sealed class HybridModelDownloadService(string dataRoot, HybridModelService registrations, HuggingFaceClient huggingFace,
    HttpClient? http = null)
{
    private const long MaximumSmallFileBytes = 16 * 1024 * 1024;
    private readonly HttpClient _http = http ?? new HttpClient(new HttpClientHandler { MaxAutomaticRedirections = 5 })
        { Timeout = TimeSpan.FromMinutes(30) };

    /// <summary>Read-only destination calculation.</summary>
    public string FolderFor(string repository) =>
        Path.Combine(Path.GetFullPath(dataRoot), "Models", "Hybrid", repository[(repository.IndexOf('/') + 1)..]);

    public async Task<HybridModelRegistration> DownloadAsync(string repository, IProgress<DownloadProgress>? progress,
        CancellationToken cancellationToken = default)
    {
        progress?.Report(new(DownloadState.Preparing, "Listing the model's files…"));
        var (revision, all) = await huggingFace.ListFilesAsync(repository, cancellationToken).ConfigureAwait(false);
        // The export is the folder's top-level files. Repository housekeeping is left out.
        var files = all.Where(file => !file.Name.Contains('/') && file.Name != ".gitattributes").ToArray();
        if (!files.Any(file => file.Name == "genai_config.json"))
            throw Problem("This repository isn't a Ryzen AI GPU + NPU export. Nothing was downloaded.");
        if (files.Any(file => file.Sha256 is null && file.GitSha1 is null))
            throw Problem("Hugging Face didn't publish fingerprints for every file, so this model can't be verified. Nothing was downloaded.");

        var folder = FolderFor(repository);
        Directory.CreateDirectory(folder);
        var total = files.Sum(file => file.LengthBytes);
        var done = 0L;
        foreach (var file in files)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var destination = Path.Combine(folder, file.Name);
            if (await IsVerifiedAsync(destination, file, cancellationToken).ConfigureAwait(false))
            {
                done += file.LengthBytes;
                continue;
            }
            var source = new Uri($"https://huggingface.co/{repository}/resolve/{revision}/{Uri.EscapeDataString(file.Name)}");
            var offset = done;
            var sink = new Progress<SetupProgress>(value => progress?.Report(new(DownloadState.Downloading,
                $"Downloading {file.Name}…", offset + (value.CompletedBytes ?? 0), total)));
            if (file.Sha256 is { } sha256)
                await PinnedRuntimeAcquisition.DownloadVerifiedAsync(_http, source, destination, file.LengthBytes, sha256, sink,
                    cancellationToken, "model file").ConfigureAwait(false);
            else
                await DownloadSmallAsync(source, destination, file, cancellationToken).ConfigureAwait(false);
            done += file.LengthBytes;
            progress?.Report(new(DownloadState.Downloading, $"Downloaded {file.Name}", done, total));
        }

        // Hugging Face's own cache layout records the commit here; the import reads the version from it.
        var metadata = Path.Combine(folder, ".cache", "huggingface", "download", "genai_config.json.metadata");
        Directory.CreateDirectory(Path.GetDirectoryName(metadata)!);
        await File.WriteAllTextAsync(metadata, revision + "\n", cancellationToken).ConfigureAwait(false);

        progress?.Report(new(DownloadState.Verifying, "Checking the model folder…", total, total));
        var registration = await registrations.RegisterAsync(folder, null, cancellationToken).ConfigureAwait(false);
        progress?.Report(new(DownloadState.Completed, "Downloaded and verified.", total, total));
        return registration;
    }

    private async Task DownloadSmallAsync(Uri source, string destination, HuggingFaceRepositoryFile file, CancellationToken token)
    {
        if (file.LengthBytes > MaximumSmallFileBytes) throw Problem("A model file is unexpectedly large. Nothing more was downloaded.");
        var bytes = await _http.GetByteArrayAsync(source, token).ConfigureAwait(false);
        if (bytes.LongLength != file.LengthBytes || GitBlobSha1(bytes) != file.GitSha1)
            throw Problem($"{file.Name} didn't match Hugging Face's fingerprint. It wasn't kept.");
        var staging = destination + ".part";
        await File.WriteAllBytesAsync(staging, bytes, token).ConfigureAwait(false);
        File.Move(staging, destination, overwrite: true);
    }

    private static async Task<bool> IsVerifiedAsync(string path, HuggingFaceRepositoryFile file, CancellationToken token)
    {
        if (!File.Exists(path) || new FileInfo(path).Length != file.LengthBytes) return false;
        if (file.Sha256 is { } sha256) return await PinnedRuntimeAcquisition.HashAsync(path, token).ConfigureAwait(false) == sha256;
        return file.LengthBytes <= MaximumSmallFileBytes && GitBlobSha1(await File.ReadAllBytesAsync(path, token).ConfigureAwait(false)) == file.GitSha1;
    }

    /// <summary>Git's blob identity: SHA-1 over "blob {length}\0" followed by the content.</summary>
    public static string GitBlobSha1(byte[] content)
    {
        using var sha1 = IncrementalHash.CreateHash(HashAlgorithmName.SHA1);
        sha1.AppendData(Encoding.UTF8.GetBytes($"blob {content.LongLength}\0"));
        sha1.AppendData(content);
        return Convert.ToHexStringLower(sha1.GetHashAndReset());
    }

    private static RuntimeException Problem(string message) => new("hybrid_download", message);
}
