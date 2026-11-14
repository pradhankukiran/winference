using System.Net;
using System.Text;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.Extensions.DependencyInjection;
using Winference.Core.Developer;
using Winference.Core.Runtime;

namespace Winference.Infrastructure.Developer;

/// <summary>One request from an app on this PC.</summary>
/// <param name="Client">The app's end of the connection, used to find which program sent it.</param>
public sealed record LocalApiRequest(string Method, string Path, IReadOnlyDictionary<string, string> Headers, byte[] Body,
    IPEndPoint Client, int ServerPort);

/// <summary>
/// The local server, on ASP.NET Core's Kestrel. It only listens on this PC's loopback addresses.
/// Web pages are refused: a browser sends an Origin header, and a page on another name
/// can't pass the Host check.
/// </summary>
public sealed class LocalApiServer : IAsyncDisposable
{
    public const int MaxBodyBytes = 16 * 1024 * 1024;
    private const int MaxConnections = 32;

    private readonly WebApplication _app;
    private bool _disposed;

    private LocalApiServer(WebApplication app, int port)
    {
        _app = app;
        Port = port;
    }

    public int Port { get; }

    /// <summary>Starts listening. Port 0 picks a free port, which tests use.</summary>
    public static async Task<LocalApiServer> StartAsync(int port, Func<LocalApiRequest, IApiResponse, CancellationToken, Task> handler)
    {
        ArgumentNullException.ThrowIfNull(handler);
        if (port is < 0 or > 65535) throw new ArgumentOutOfRangeException(nameof(port));
        // Some tools try "localhost" as ::1 first, so IPv6 is added when it's free. IPv4 is required.
        Exception? failure = null;
        foreach (var withIpv6 in port == 0 ? new[] { false } : new[] { true, false })
        {
            var app = Build(port, withIpv6, handler);
            try
            {
                await app.StartAsync().ConfigureAwait(false);
                var bound = app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses
                    .Select(address => new Uri(address).Port).First();
                return new LocalApiServer(app, bound);
            }
            catch (Exception error) when (error is IOException or System.Net.Sockets.SocketException or InvalidOperationException)
            {
                failure = error;
                await app.DisposeAsync().ConfigureAwait(false);
            }
        }
        throw new RuntimeException("port_unavailable", $"Port {port} is in use or reserved by Windows. Choose another port.", failure);
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed) return;
        _disposed = true;
        // Open requests are cancelled; one that ignores it must not hold up closing.
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        try { await _app.StopAsync(deadline.Token).ConfigureAwait(false); }
        catch (OperationCanceledException) { }
        await _app.DisposeAsync().ConfigureAwait(false);
    }

    private static WebApplication Build(int port, bool withIpv6, Func<LocalApiRequest, IApiResponse, CancellationToken, Task> handler)
    {
        var builder = WebApplication.CreateEmptyBuilder(new WebApplicationOptions());
        builder.WebHost.UseKestrelCore().ConfigureKestrel(options =>
        {
            options.AddServerHeader = false;
            options.Limits.MaxRequestBodySize = MaxBodyBytes;
            options.Limits.MaxConcurrentConnections = MaxConnections;
            options.Listen(IPAddress.Loopback, port);
            if (withIpv6) options.Listen(IPAddress.IPv6Loopback, port);
        });
        var app = builder.Build();
        app.Run(context => ServeAsync(context, handler));
        return app;
    }

    private static async Task ServeAsync(HttpContext context, Func<LocalApiRequest, IApiResponse, CancellationToken, Task> handler)
    {
        var response = new ContextResponse(context.Response);
        var cancellation = context.RequestAborted;
        try
        {
            var headers = context.Request.Headers.ToDictionary(pair => pair.Key.ToLowerInvariant(), pair => pair.Value.ToString(),
                StringComparer.OrdinalIgnoreCase);
            if (Refusal(headers) is { } refusal)
            {
                await SendErrorAsync(response, 403, "forbidden", refusal, cancellation).ConfigureAwait(false);
                return;
            }
            using var body = new MemoryStream();
            await context.Request.Body.CopyToAsync(body, cancellation).ConfigureAwait(false);
            var connection = context.Connection;
            var request = new LocalApiRequest(context.Request.Method.ToUpperInvariant(), context.Request.Path.Value ?? "/", headers,
                body.ToArray(), new IPEndPoint(connection.RemoteIpAddress!, connection.RemotePort), connection.LocalPort);
            await handler(request, response, cancellation).ConfigureAwait(false);
            if (!response.HasStarted) context.Response.StatusCode = StatusCodes.Status204NoContent;
        }
        catch (ApiException error)
        {
            await SendErrorAsync(response, error.Status, error.Code, error.Message, cancellation).ConfigureAwait(false);
        }
        catch (BadHttpRequestException error)
        {
            var message = error.StatusCode == StatusCodes.Status413PayloadTooLarge ? "The request body is larger than 16 MB." : "The request couldn't be read.";
            await SendErrorAsync(response, error.StatusCode, "bad_request", message, cancellation).ConfigureAwait(false);
        }
        catch (Exception) when (cancellation.IsCancellationRequested) { /* The app hung up. */ }
        catch (IOException) { }
        catch (Exception)
        {
            await SendErrorAsync(response, 500, "server_error", "Winference couldn't handle this request.", cancellation).ConfigureAwait(false);
        }
    }

    private static async Task SendErrorAsync(ContextResponse response, int status, string code, string message, CancellationToken cancellationToken)
    {
        if (response.HasStarted) return;
        try { await response.SendJsonAsync(status, OpenAiApi.Error(status, code, message), cancellationToken).ConfigureAwait(false); }
        catch (Exception) { /* The app already left. */ }
    }

    /// <summary>Why a request is refused before it reaches the handler, or null.</summary>
    internal static string? Refusal(IReadOnlyDictionary<string, string> headers)
    {
        if (headers.ContainsKey("origin"))
            return "Web pages can't use the Winference server. Call it from an app on this PC.";
        if (!headers.TryGetValue("host", out var host) || !IsLocalHost(host))
            return "Use 127.0.0.1 or localhost to reach the Winference server.";
        return null;
    }

    private static bool IsLocalHost(string host)
    {
        host = host.Trim().ToLowerInvariant();
        string name;
        if (host.StartsWith('['))
        {
            var close = host.IndexOf(']');
            if (close < 0) return false;
            name = host[1..close];
        }
        else
        {
            var colon = host.LastIndexOf(':');
            name = colon >= 0 ? host[..colon] : host;
        }
        return name is "localhost" or "127.0.0.1" or "::1";
    }

    /// <summary>Plain replies carry a length; Kestrel sends streamed ones in chunks.</summary>
    private sealed class ContextResponse(HttpResponse response) : IApiResponse
    {
        private bool _streaming;

        public bool HasStarted { get; private set; }

        public async Task SendJsonAsync(int status, string json, CancellationToken cancellationToken)
        {
            if (HasStarted) throw new InvalidOperationException("A reply was already sent.");
            HasStarted = true;
            var body = Encoding.UTF8.GetBytes(json);
            response.StatusCode = status;
            response.ContentType = "application/json; charset=utf-8";
            response.ContentLength = body.Length;
            response.Headers.CacheControl = "no-store";
            await response.Body.WriteAsync(body, cancellationToken).ConfigureAwait(false);
        }

        public async Task BeginEventStreamAsync(CancellationToken cancellationToken)
        {
            if (HasStarted) throw new InvalidOperationException("A reply was already sent.");
            HasStarted = _streaming = true;
            response.StatusCode = StatusCodes.Status200OK;
            response.ContentType = "text/event-stream; charset=utf-8";
            response.Headers.CacheControl = "no-cache";
            await response.StartAsync(cancellationToken).ConfigureAwait(false);
        }

        public async Task SendEventAsync(string data, CancellationToken cancellationToken)
        {
            if (!_streaming) throw new InvalidOperationException("The event stream isn't open.");
            await response.Body.WriteAsync(Encoding.UTF8.GetBytes("data: " + data + "\n\n"), cancellationToken).ConfigureAwait(false);
            await response.Body.FlushAsync(cancellationToken).ConfigureAwait(false);
        }
    }
}
