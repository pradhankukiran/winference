using System.Net;
using System.Net.Http.Headers;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using Winference.Core.Developer;
using Winference.Core.Runtime;
using Winference.Infrastructure.Developer;

namespace Winference.Tests;

public sealed class OpenAiApiTests
{
    private static ApiChatRequest Parse(string json) => OpenAiApi.ParseChatRequest(Encoding.UTF8.GetBytes(json));

    [Fact]
    public void Text_and_part_messages_become_chat_messages()
    {
        var request = Parse("""
            {"model":"x","stream":true,"stream_options":{"include_usage":true},"max_tokens":50,"temperature":0.3,
             "messages":[{"role":"developer","content":"Be brief."},
                         {"role":"assistant","content":null},
                         {"role":"user","content":[{"type":"text","text":"Hello"},{"type":"text","text":"there"}]}]}
            """);
        Assert.Equal(["system", "user"], request.Messages.Select(message => message.Role));
        Assert.Equal("Hello\nthere", request.Messages[1].Content);
        Assert.True(request.Stream);
        Assert.True(request.IncludeUsage);
        Assert.Equal(50, request.MaxTokens);
        Assert.Equal(0.3, request.Temperature);
        Assert.Equal("x", request.Model);
    }

    [Fact]
    public void Pictures_must_be_data_urls()
    {
        var png = Convert.ToBase64String([1, 2, 3]);
        var request = Parse($$$"""{"messages":[{"role":"user","content":[{"type":"text","text":"What is it?"},{"type":"image_url","image_url":{"url":"data:image/png;base64,{{{png}}}"}}]}]}""");
        Assert.Equal("image/png", request.Messages[0].Images![0].MediaType);
        var web = Assert.Throws<ApiException>(() => Parse("""{"messages":[{"role":"user","content":[{"type":"text","text":"Hi"},{"type":"image_url","image_url":{"url":"https://example.com/a.png"}}]}]}"""));
        Assert.Equal(400, web.Status);
    }

    [Theory]
    [InlineData("""{"messages":[]}""")]
    [InlineData("""{"messages":[{"role":"user","content":"hi"},{"role":"assistant","content":"hello"}]}""")]
    [InlineData("""{"messages":[{"role":"tool","content":"42"},{"role":"user","content":"hi"}]}""")]
    [InlineData("""{"messages":[{"role":"user","content":"hi"}],"temperature":3}""")]
    [InlineData("""{"messages":[{"role":"user","content":"hi"}],"max_tokens":0}""")]
    [InlineData("""{"messages":[{"role":"user","content":"hi"}],"n":2}""")]
    [InlineData("""not json""")]
    public void Unsupported_requests_are_refused_with_400(string json)
    {
        var error = Assert.Throws<ApiException>(() => Parse(json));
        Assert.Equal(400, error.Status);
    }

    [Fact]
    public void Long_histories_keep_the_system_message_and_newest_turns()
    {
        var turns = Enumerable.Range(0, 50).SelectMany(i => new[]
        {
            $$"""{"role":"user","content":"q{{i}}"}""", $$"""{"role":"assistant","content":"a{{i}}"}"""
        });
        var json = $$"""{"messages":[{"role":"system","content":"rules"},{{string.Join(',', turns)}},{"role":"user","content":"last"}]}""";
        var request = Parse(json);
        Assert.True(request.Messages.Count <= OpenAiApi.MaxMessages);
        Assert.Equal("system", request.Messages[0].Role);
        Assert.Equal("user", request.Messages[1].Role);
        Assert.Equal("last", request.Messages[^1].Content);
    }

    [Fact]
    public void Preview_is_one_short_line()
    {
        var request = Parse("""{"messages":[{"role":"user","content":"Explain\n\nthis   code please"}]}""");
        Assert.Equal("Explain this code please", OpenAiApi.Preview(request));
        Assert.Equal(10, OpenAiApi.Preview(request, 10).Length);
    }
}

public sealed class ChatCompletionResponderTests
{
    private static async Task<(ApiTarget Target, FakeBackend Backend)> ReadyAsync()
    {
        var backend = new FakeBackend();
        var broker = new RuntimeBroker(backend, TimeSpan.FromSeconds(5));
        var profile = TestProfiles.Create();
        await broker.StartAsync(profile);
        return (new ApiTarget(broker, "my-model", profile), backend);
    }

    private static ApiChatRequest Request(bool stream, int? maxTokens = null) =>
        new([new ChatMessage("user", "Hi")], maxTokens, null, stream, IncludeUsage: true, Model: null);

    [Fact]
    public async Task A_whole_reply_is_one_json_answer()
    {
        var (target, _) = await ReadyAsync();
        var response = new RecordingResponse();
        var outcome = await ChatCompletionResponder.RespondAsync(target, Request(stream: false), response, 0.7, null,
            TimeSpan.FromSeconds(1), CancellationToken.None);
        Assert.Equal(200, outcome.Status);
        Assert.Equal(200, response.Status);
        using var json = JsonDocument.Parse(response.Json!);
        var choice = json.RootElement.GetProperty("choices")[0];
        Assert.Equal("hello", choice.GetProperty("message").GetProperty("content").GetString());
        Assert.Equal("stop", choice.GetProperty("finish_reason").GetString());
        Assert.Equal("my-model", json.RootElement.GetProperty("model").GetString());
    }

    [Fact]
    public async Task A_streamed_reply_sends_role_text_finish_usage_and_done()
    {
        var (target, _) = await ReadyAsync();
        var response = new RecordingResponse();
        var meter = new ReplyMeter();
        await ChatCompletionResponder.RespondAsync(target, Request(stream: true), response, 0.7, meter,
            TimeSpan.FromSeconds(1), CancellationToken.None);
        Assert.True(response.Streaming);
        Assert.Equal("[DONE]", response.Events[^1]);
        Assert.Contains(response.Events, e => e.Contains("\"role\":\"assistant\""));
        Assert.Contains(response.Events, e => e.Contains("\"content\":\"hello\""));
        Assert.Contains(response.Events, e => e.Contains("\"finish_reason\":\"stop\""));
        Assert.Contains(response.Events, e => e.Contains("\"usage\""));
        Assert.False(meter.IsActive);
    }

    [Fact]
    public async Task Requested_limits_never_exceed_the_model_profile()
    {
        var (target, backend) = await ReadyAsync();
        await ChatCompletionResponder.RespondAsync(target, Request(stream: false, maxTokens: 100_000), new RecordingResponse(), 0.7, null,
            TimeSpan.FromSeconds(1), CancellationToken.None);
        Assert.Equal(target.Profile.MaxOutputTokens, backend.LastRequest!.MaxOutputTokens);
    }

    [Fact]
    public async Task A_busy_model_is_waited_for_then_refused()
    {
        var (target, backend) = await ReadyAsync();
        backend.HoldReply = true;
        var chat = target.Broker.GenerateAsync([new ChatMessage("user", "first")]).GetAsyncEnumerator();
        await chat.MoveNextAsync();
        var error = await Assert.ThrowsAsync<ApiException>(() => ChatCompletionResponder.RespondAsync(target, Request(stream: false),
            new RecordingResponse(), 0.7, null, TimeSpan.FromMilliseconds(600), CancellationToken.None));
        Assert.Equal(503, error.Status);
        await chat.DisposeAsync();
    }

    [Fact]
    public async Task An_unloaded_model_is_reported_as_unavailable()
    {
        var broker = new RuntimeBroker(new FakeBackend());
        var target = new ApiTarget(broker, "m", TestProfiles.Create());
        var error = await Assert.ThrowsAsync<ApiException>(() => ChatCompletionResponder.RespondAsync(target, Request(stream: true),
            new RecordingResponse(), 0.7, null, TimeSpan.FromSeconds(1), CancellationToken.None));
        Assert.Equal(503, error.Status);
    }

    private sealed class RecordingResponse : IApiResponse
    {
        public bool HasStarted { get; private set; }
        public bool Streaming { get; private set; }
        public int? Status { get; private set; }
        public string? Json { get; private set; }
        public List<string> Events { get; } = [];

        public Task SendJsonAsync(int status, string json, CancellationToken cancellationToken)
        {
            HasStarted = true;
            Status = status;
            Json = json;
            return Task.CompletedTask;
        }

        public Task BeginEventStreamAsync(CancellationToken cancellationToken)
        {
            HasStarted = Streaming = true;
            return Task.CompletedTask;
        }

        public Task SendEventAsync(string data, CancellationToken cancellationToken)
        {
            Events.Add(data);
            return Task.CompletedTask;
        }
    }
}

public sealed class LocalApiServerTests
{
    private static HttpClient Client(LocalApiServer server) => new() { BaseAddress = new Uri($"http://127.0.0.1:{server.Port}/") };

    [Fact]
    public async Task Requests_reach_the_handler_with_their_body()
    {
        LocalApiRequest? seen = null;
        await using var server = LocalApiServer.Start(0, async (request, response, token) =>
        {
            seen = request;
            await response.SendJsonAsync(200, """{"ok":true}""", token);
        });
        using var client = Client(server);
        var reply = await client.PostAsync("v1/chat/completions?x=1", new StringContent("""{"a":1}""", Encoding.UTF8, "application/json"));
        Assert.Equal(HttpStatusCode.OK, reply.StatusCode);
        Assert.Equal("""{"ok":true}""", await reply.Content.ReadAsStringAsync());
        Assert.Equal("POST", seen!.Method);
        Assert.Equal("/v1/chat/completions", seen.Path);
        Assert.Equal("""{"a":1}""", Encoding.UTF8.GetString(seen.Body));
        Assert.Equal(server.Port, seen.ServerPort);
    }

    [Fact]
    public async Task Streams_arrive_as_server_sent_events()
    {
        await using var server = LocalApiServer.Start(0, async (_, response, token) =>
        {
            await response.BeginEventStreamAsync(token);
            await response.SendEventAsync("""{"n":1}""", token);
            await response.SendEventAsync("[DONE]", token);
        });
        using var client = Client(server);
        using var reply = await client.GetAsync("v1/stream", HttpCompletionOption.ResponseHeadersRead);
        Assert.Equal("text/event-stream", reply.Content.Headers.ContentType!.MediaType);
        var body = await reply.Content.ReadAsStringAsync();
        Assert.Equal("data: {\"n\":1}\n\ndata: [DONE]\n\n", body);
    }

    [Fact]
    public async Task Web_pages_and_other_host_names_are_refused()
    {
        var called = false;
        await using var server = LocalApiServer.Start(0, (_, response, token) =>
        {
            called = true;
            return response.SendJsonAsync(200, "{}", token);
        });
        using var client = Client(server);
        using var fromPage = new HttpRequestMessage(HttpMethod.Get, "v1/models");
        fromPage.Headers.Add("Origin", "https://example.com");
        Assert.Equal(HttpStatusCode.Forbidden, (await client.SendAsync(fromPage)).StatusCode);
        using var rebound = new HttpRequestMessage(HttpMethod.Get, "v1/models");
        rebound.Headers.Host = "attacker.example";
        Assert.Equal(HttpStatusCode.Forbidden, (await client.SendAsync(rebound)).StatusCode);
        Assert.False(called);
    }

    [Fact]
    public async Task Handler_errors_become_openai_errors()
    {
        await using var server = LocalApiServer.Start(0, (_, _, _) => throw new ApiException(404, "not_found", "Nothing here."));
        using var client = Client(server);
        var reply = await client.GetAsync("v1/nothing");
        Assert.Equal(HttpStatusCode.NotFound, reply.StatusCode);
        using var json = JsonDocument.Parse(await reply.Content.ReadAsStringAsync());
        Assert.Equal("Nothing here.", json.RootElement.GetProperty("error").GetProperty("message").GetString());
    }

    [Fact]
    public async Task An_app_hanging_up_cancels_its_request()
    {
        var cancelled = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var server = LocalApiServer.Start(0, async (_, _, token) =>
        {
            using var registration = token.Register(() => cancelled.TrySetResult());
            started.TrySetResult();
            await Task.Delay(Timeout.Infinite, token);
        });
        using (var socket = new TcpClient())
        {
            await socket.ConnectAsync(IPAddress.Loopback, server.Port);
            var stream = socket.GetStream();
            await stream.WriteAsync(Encoding.ASCII.GetBytes("GET /v1/models HTTP/1.1\r\nHost: localhost\r\n\r\n"));
            await started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        }
        await cancelled.Task.WaitAsync(TimeSpan.FromSeconds(5));
    }

    [Fact]
    public void A_taken_port_gives_a_clear_message()
    {
        using var taken = new TcpListener(IPAddress.Loopback, 0);
        taken.Start();
        var port = ((IPEndPoint)taken.LocalEndpoint).Port;
        var error = Assert.Throws<RuntimeException>(() => LocalApiServer.Start(port, (_, _, _) => Task.CompletedTask));
        Assert.Equal("port_unavailable", error.Code);
    }

    [Fact]
    public async Task The_calling_program_is_identified()
    {
        if (!OperatingSystem.IsWindows()) return;
        CallerApp? caller = null;
        await using var server = LocalApiServer.Start(0, (request, response, token) =>
        {
            caller = CallerLookup.Identify(request.Client, request.ServerPort);
            return response.SendJsonAsync(200, "{}", token);
        });
        using var client = Client(server);
        (await client.GetAsync("v1/models")).EnsureSuccessStatusCode();
        Assert.Equal(Environment.ProcessId, caller!.ProcessId);
        Assert.NotNull(caller.Path);
        Assert.Equal(caller.Path!.ToLowerInvariant(), caller.Key);
    }
}

public sealed class AppPermissionStoreTests
{
    [Fact]
    public void Answers_are_remembered_across_restarts()
    {
        using var folder = new TemporaryFolder();
        var store = new AppPermissionStore(folder.Path);
        store.Set(new("c:\\code.exe", "Code", "C:\\Code.exe", true, DateTimeOffset.Now));
        store.Set(new("c:\\bad.exe", "Bad", null, false, DateTimeOffset.Now));
        store.MarkUsed("c:\\code.exe", DateTimeOffset.Now);

        var reopened = new AppPermissionStore(folder.Path);
        Assert.True(reopened.Find("c:\\code.exe")!.Allowed);
        Assert.NotNull(reopened.Find("c:\\code.exe")!.LastUsed);
        Assert.False(reopened.Find("c:\\bad.exe")!.Allowed);
        reopened.Remove("c:\\bad.exe");
        Assert.Null(new AppPermissionStore(folder.Path).Find("c:\\bad.exe"));
    }

    [Fact]
    public void A_damaged_file_grants_nothing()
    {
        using var folder = new TemporaryFolder();
        File.WriteAllText(Path.Combine(folder.Path, "developer-apps.json"), "{ not json");
        var store = new AppPermissionStore(folder.Path);
        Assert.Empty(store.All());
        Assert.Null(store.Find("anything"));
    }
}
