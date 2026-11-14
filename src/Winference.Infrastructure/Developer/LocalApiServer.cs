using System.Net;
using System.Net.Sockets;
using System.Text;
using Winference.Core.Developer;
using Winference.Core.Runtime;

namespace Winference.Infrastructure.Developer;

/// <summary>One request from an app on this PC.</summary>
/// <param name="Client">The app's end of the connection, used to find which program sent it.</param>
public sealed record LocalApiRequest(string Method, string Path, IReadOnlyDictionary<string, string> Headers, byte[] Body,
    IPEndPoint Client, int ServerPort);

/// <summary>
/// A small HTTP/1.1 server that only listens on this PC's loopback addresses. It reads one
/// request per connection and closes it after the reply. Web pages are refused: a browser
/// sends an Origin header, and a page on another name can't pass the Host check.
/// </summary>
public sealed class LocalApiServer : IAsyncDisposable
{
    public const int MaxBodyBytes = 16 * 1024 * 1024;
    private const int MaxHeaderBytes = 32 * 1024;
    private const int MaxConnections = 32;
    private static readonly TimeSpan HeaderTimeout = TimeSpan.FromSeconds(30);

    private readonly Func<LocalApiRequest, IApiResponse, CancellationToken, Task> _handler;
    private readonly List<TcpListener> _listeners = [];
    private readonly CancellationTokenSource _stop = new();
    private readonly SemaphoreSlim _slots = new(MaxConnections, MaxConnections);
    private readonly List<Task> _loops = [];
    private readonly HashSet<Task> _connections = [];
    private bool _disposed;

    private LocalApiServer(int port, Func<LocalApiRequest, IApiResponse, CancellationToken, Task> handler)
    {
        Port = port;
        _handler = handler;
    }

    public int Port { get; }

    /// <summary>Starts listening. Port 0 picks a free port, which tests use.</summary>
    public static LocalApiServer Start(int port, Func<LocalApiRequest, IApiResponse, CancellationToken, Task> handler)
    {
        ArgumentNullException.ThrowIfNull(handler);
        if (port is < 0 or > 65535) throw new ArgumentOutOfRangeException(nameof(port));
        var v4 = new TcpListener(IPAddress.Loopback, port);
        try { v4.Start(); }
        catch (SocketException error)
        {
            v4.Dispose();
            throw new RuntimeException("port_unavailable",
                $"Port {port} is in use or reserved by Windows. Choose another port.", error);
        }
        var actual = ((IPEndPoint)v4.LocalEndpoint).Port;
        var server = new LocalApiServer(actual, handler);
        server._listeners.Add(v4);
        // Some tools try "localhost" as ::1 first. IPv6 is optional; IPv4 always works.
        if (Socket.OSSupportsIPv6)
        {
            var v6 = new TcpListener(IPAddress.IPv6Loopback, actual);
            try
            {
                v6.Start();
                server._listeners.Add(v6);
            }
            catch (SocketException) { v6.Dispose(); }
        }
        foreach (var listener in server._listeners) server._loops.Add(server.AcceptLoopAsync(listener));
        return server;
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed) return;
        _disposed = true;
        _stop.Cancel();
        foreach (var listener in _listeners) listener.Stop();
        try { await Task.WhenAll(_loops).ConfigureAwait(false); }
        catch (Exception) { /* Each loop ends by itself once its listener stops. */ }
        Task[] open;
        lock (_connections) open = [.. _connections];
        try { await Task.WhenAll(open).WaitAsync(TimeSpan.FromSeconds(10)).ConfigureAwait(false); }
        catch (Exception) { /* A request that ignores cancellation must not hold up closing. */ }
        foreach (var listener in _listeners) listener.Dispose();
    }

    private async Task AcceptLoopAsync(TcpListener listener)
    {
        while (!_stop.IsCancellationRequested)
        {
            TcpClient client;
            try { client = await listener.AcceptTcpClientAsync(_stop.Token).ConfigureAwait(false); }
            catch (Exception) when (_stop.IsCancellationRequested) { return; }
            catch (SocketException) { continue; }
            catch (ObjectDisposedException) { return; }
            var connection = ServeAsync(client);
            lock (_connections) _connections.Add(connection);
            _ = connection.ContinueWith(done => { lock (_connections) _connections.Remove(done); }, TaskScheduler.Default);
        }
    }

    private async Task ServeAsync(TcpClient client)
    {
        using var owned = client;
        client.NoDelay = true;
        var stream = client.GetStream();
        var response = new ConnectionResponse(stream);
        if (!await _slots.WaitAsync(0).ConfigureAwait(false))
        {
            await TrySendErrorAsync(response, 503, "server_busy", "Too many open requests. Try again shortly.").ConfigureAwait(false);
            return;
        }
        using var connection = CancellationTokenSource.CreateLinkedTokenSource(_stop.Token);
        try
        {
            ParsedRequest parsed;
            using (var headerDeadline = CancellationTokenSource.CreateLinkedTokenSource(connection.Token))
            {
                headerDeadline.CancelAfter(HeaderTimeout);
                parsed = await ReadRequestAsync(stream, headerDeadline.Token).ConfigureAwait(false);
            }
            if (Refusal(parsed.Headers) is { } refusal)
            {
                await TrySendErrorAsync(response, 403, "forbidden", refusal).ConfigureAwait(false);
                return;
            }
            var request = new LocalApiRequest(parsed.Method, parsed.Path, parsed.Headers, parsed.Body,
                (IPEndPoint)client.Client.RemoteEndPoint!, Port);
            // A finished upload means the app has nothing more to send; a read that ends means it left.
            _ = WatchForHangUpAsync(stream, connection);
            await _handler(request, response, connection.Token).ConfigureAwait(false);
            await response.CompleteAsync(connection.Token).ConfigureAwait(false);
        }
        catch (ApiException error)
        {
            await TrySendErrorAsync(response, error.Status, error.Code, error.Message).ConfigureAwait(false);
        }
        catch (Exception) when (connection.IsCancellationRequested) { }
        catch (OperationCanceledException) { /* The app sent nothing for too long. */ }
        catch (IOException) { }
        catch (Exception)
        {
            await TrySendErrorAsync(response, 500, "server_error", "Winference couldn't handle this request.").ConfigureAwait(false);
        }
        finally
        {
            connection.Cancel();
            _slots.Release();
        }
    }

    private static async Task WatchForHangUpAsync(NetworkStream stream, CancellationTokenSource connection)
    {
        var buffer = new byte[1];
        try
        {
            while (!connection.IsCancellationRequested)
                if (await stream.ReadAsync(buffer, connection.Token).ConfigureAwait(false) == 0) break;
        }
        catch (Exception) { /* Any failure to read means the app is gone. */ }
        try { connection.Cancel(); }
        catch (ObjectDisposedException) { }
    }

    private static async Task TrySendErrorAsync(ConnectionResponse response, int status, string code, string message)
    {
        if (response.HasStarted) return;
        try
        {
            using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            await response.SendJsonAsync(status, OpenAiApi.Error(status, code, message), deadline.Token).ConfigureAwait(false);
        }
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

    private sealed record ParsedRequest(string Method, string Path, Dictionary<string, string> Headers, byte[] Body);

    private static async Task<ParsedRequest> ReadRequestAsync(NetworkStream stream, CancellationToken cancellationToken)
    {
        var buffer = new byte[8192];
        var head = new MemoryStream();
        var end = -1;
        while (end < 0)
        {
            var read = await stream.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
            if (read == 0) throw new IOException("The connection closed before a full request arrived.");
            head.Write(buffer, 0, read);
            end = IndexOfHeaderEnd(head.GetBuffer(), (int)head.Length);
            if (end < 0 && head.Length > MaxHeaderBytes)
                throw new ApiException(431, "headers_too_large", "The request headers are too large.");
        }
        var all = head.GetBuffer();
        var text = Encoding.ASCII.GetString(all, 0, end);
        var lines = text.Split("\r\n");
        var start = lines[0].Split(' ');
        if (start.Length != 3 || !start[2].StartsWith("HTTP/1.", StringComparison.Ordinal))
            throw new ApiException(400, "bad_request", "The request line couldn't be read.");
        var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var line in lines.Skip(1))
        {
            var colon = line.IndexOf(':');
            if (colon <= 0) continue;
            var name = line[..colon].Trim().ToLowerInvariant();
            var value = line[(colon + 1)..].Trim();
            headers[name] = headers.TryGetValue(name, out var earlier) ? earlier + ", " + value : value;
        }
        if (headers.ContainsKey("transfer-encoding"))
            throw new ApiException(411, "length_required", "Send the request body with a Content-Length header.");
        var length = 0L;
        if (headers.TryGetValue("content-length", out var lengthText) && (!long.TryParse(lengthText, out length) || length < 0))
            throw new ApiException(400, "bad_request", "The Content-Length header isn't a valid number.");
        if (length > MaxBodyBytes) throw new ApiException(413, "body_too_large", "The request body is larger than 16 MB.");

        var bodyStart = end + 4;
        var body = new byte[length];
        var have = (int)Math.Min(length, head.Length - bodyStart);
        Array.Copy(all, bodyStart, body, 0, have);
        if (have < length && headers.TryGetValue("expect", out var expect) && expect.Equals("100-continue", StringComparison.OrdinalIgnoreCase))
        {
            await stream.WriteAsync("HTTP/1.1 100 Continue\r\n\r\n"u8.ToArray(), cancellationToken).ConfigureAwait(false);
        }
        while (have < length)
        {
            var read = await stream.ReadAsync(body.AsMemory(have), cancellationToken).ConfigureAwait(false);
            if (read == 0) throw new IOException("The connection closed before the full body arrived.");
            have += read;
        }
        var target = start[1];
        var query = target.IndexOf('?');
        return new(start[0].ToUpperInvariant(), query >= 0 ? target[..query] : target, headers, body);
    }

    private static int IndexOfHeaderEnd(byte[] data, int count)
    {
        for (var i = 0; i + 3 < count; i++)
            if (data[i] == '\r' && data[i + 1] == '\n' && data[i + 2] == '\r' && data[i + 3] == '\n') return i;
        return -1;
    }

    /// <summary>Plain replies carry a length; streamed ones use chunks so apps can tell a clean end from a cut.</summary>
    private sealed class ConnectionResponse(NetworkStream stream) : IApiResponse
    {
        private bool _streaming;
        private bool _completed;

        public bool HasStarted { get; private set; }

        public async Task SendJsonAsync(int status, string json, CancellationToken cancellationToken)
        {
            if (HasStarted) throw new InvalidOperationException("A reply was already sent.");
            HasStarted = true;
            _completed = true;
            var body = Encoding.UTF8.GetBytes(json);
            var head = $"HTTP/1.1 {status} {Reason(status)}\r\nContent-Type: application/json; charset=utf-8\r\n" +
                $"Content-Length: {body.Length}\r\nCache-Control: no-store\r\nConnection: close\r\n\r\n";
            await stream.WriteAsync(Encoding.ASCII.GetBytes(head), cancellationToken).ConfigureAwait(false);
            await stream.WriteAsync(body, cancellationToken).ConfigureAwait(false);
            await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
        }

        public async Task BeginEventStreamAsync(CancellationToken cancellationToken)
        {
            if (HasStarted) throw new InvalidOperationException("A reply was already sent.");
            HasStarted = true;
            _streaming = true;
            const string Head = "HTTP/1.1 200 OK\r\nContent-Type: text/event-stream; charset=utf-8\r\nCache-Control: no-cache\r\n" +
                "Transfer-Encoding: chunked\r\nConnection: close\r\n\r\n";
            await stream.WriteAsync(Encoding.ASCII.GetBytes(Head), cancellationToken).ConfigureAwait(false);
            await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
        }

        public async Task SendEventAsync(string data, CancellationToken cancellationToken)
        {
            if (!_streaming || _completed) throw new InvalidOperationException("The event stream isn't open.");
            var payload = Encoding.UTF8.GetBytes("data: " + data + "\n\n");
            await stream.WriteAsync(Encoding.ASCII.GetBytes($"{payload.Length:X}\r\n"), cancellationToken).ConfigureAwait(false);
            await stream.WriteAsync(payload, cancellationToken).ConfigureAwait(false);
            await stream.WriteAsync("\r\n"u8.ToArray(), cancellationToken).ConfigureAwait(false);
            await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
        }

        public async Task CompleteAsync(CancellationToken cancellationToken)
        {
            if (_completed) return;
            _completed = true;
            if (!_streaming)
            {
                HasStarted = true;
                const string Empty = "HTTP/1.1 204 No Content\r\nConnection: close\r\n\r\n";
                await stream.WriteAsync(Encoding.ASCII.GetBytes(Empty), cancellationToken).ConfigureAwait(false);
                return;
            }
            await stream.WriteAsync("0\r\n\r\n"u8.ToArray(), cancellationToken).ConfigureAwait(false);
            await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
        }

        private static string Reason(int status) => status switch
        {
            200 => "OK", 400 => "Bad Request", 403 => "Forbidden", 404 => "Not Found", 405 => "Method Not Allowed",
            411 => "Length Required", 413 => "Content Too Large", 431 => "Request Header Fields Too Large",
            500 => "Internal Server Error", 503 => "Service Unavailable", _ => "Status"
        };
    }
}
