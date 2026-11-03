using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using Winference.Core.Runtime;
using Winference.Core.Setup;
using Winference.Infrastructure.Setup;

namespace Winference.Tests;

public class DownloadRecoveryTests
{
    private static readonly byte[] Payload = RandomNumberGenerator.GetBytes(256 * 1024);
    private static readonly string PayloadHash = Convert.ToHexStringLower(SHA256.HashData(Payload));
    private static readonly Uri Source = new("https://example.test/model.bin");

    /// <summary>Serves the payload, honouring Range requests, and can cut the connection partway.</summary>
    private sealed class FakeServer(byte[] content) : HttpMessageHandler
    {
        public long? FailAfterBytes { get; set; }
        public CancellationTokenSource? CancelAfterFailure { get; set; }
        public RangeHeaderValue? LastRange { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            LastRange = request.Headers.Range;
            var offset = (int)(request.Headers.Range?.Ranges.Single().From ?? 0);
            var response = new HttpResponseMessage(offset > 0 ? HttpStatusCode.PartialContent : HttpStatusCode.OK)
            {
                Content = new StreamContent(new ThrottledStream(content[offset..], FailAfterBytes, CancelAfterFailure))
            };
            if (offset > 0) response.Content.Headers.ContentRange = new(offset, content.Length - 1, content.Length);
            return Task.FromResult(response);
        }
    }

    private sealed class ThrottledStream(byte[] content, long? failAfter, CancellationTokenSource? cancel) : MemoryStream(content)
    {
        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (failAfter is { } limit && Position >= limit)
            {
                if (cancel is not null)
                {
                    await cancel.CancelAsync();
                    cancellationToken.ThrowIfCancellationRequested();
                }
                throw new IOException("connection reset");
            }
            var slice = failAfter is { } most ? buffer[..(int)Math.Min(buffer.Length, Math.Max(1, most - Position))] : buffer;
            return await base.ReadAsync(slice, cancellationToken);
        }
    }

    private static readonly IProgress<SetupProgress> NoProgress = new Progress<SetupProgress>();

    [Fact]
    public async Task Cancelling_keeps_the_bytes_and_nothing_is_published()
    {
        using var folder = new TemporaryFolder();
        var destination = Path.Combine(folder.Path, "model.bin");
        using var cancel = new CancellationTokenSource();
        using var client = new HttpClient(new FakeServer(Payload) { FailAfterBytes = 100_000, CancelAfterFailure = cancel });

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => PinnedRuntimeAcquisition.DownloadVerifiedAsync(
            client, Source, destination, Payload.Length, PayloadHash, NoProgress, cancel.Token));

        Assert.False(File.Exists(destination));
        Assert.True(new FileInfo(destination + ".part").Length >= 100_000);
    }

    [Fact]
    public async Task A_retry_resumes_from_the_saved_bytes_and_verifies_the_whole_file()
    {
        using var folder = new TemporaryFolder();
        var destination = Path.Combine(folder.Path, "model.bin");
        await File.WriteAllBytesAsync(destination + ".part", Payload[..100_000]);
        var server = new FakeServer(Payload);
        using var client = new HttpClient(server);

        await PinnedRuntimeAcquisition.DownloadVerifiedAsync(client, Source, destination, Payload.Length, PayloadHash, NoProgress, CancellationToken.None);

        Assert.Equal(100_000, server.LastRange!.Ranges.Single().From);
        Assert.Equal(Payload, await File.ReadAllBytesAsync(destination));
        Assert.False(File.Exists(destination + ".part"));
    }

    [Fact]
    public async Task A_corrupted_download_is_never_published()
    {
        using var folder = new TemporaryFolder();
        var destination = Path.Combine(folder.Path, "model.bin");
        var corrupted = (byte[])Payload.Clone();
        corrupted[^1] ^= 0xFF;
        using var client = new HttpClient(new FakeServer(corrupted));

        await Assert.ThrowsAsync<RuntimeException>(() => PinnedRuntimeAcquisition.DownloadVerifiedAsync(
            client, Source, destination, Payload.Length, PayloadHash, NoProgress, CancellationToken.None));

        Assert.False(File.Exists(destination));
    }

    [Fact]
    public async Task A_finished_file_is_not_downloaded_again()
    {
        using var folder = new TemporaryFolder();
        var destination = Path.Combine(folder.Path, "model.bin");
        await File.WriteAllBytesAsync(destination, Payload);
        var server = new FakeServer(Payload);
        using var client = new HttpClient(server);

        await PinnedRuntimeAcquisition.DownloadVerifiedAsync(client, Source, destination, Payload.Length, PayloadHash, NoProgress, CancellationToken.None);

        Assert.Null(server.LastRange);
    }
}
