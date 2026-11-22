using System.Text;
using Winference.Core.Runtime;
using Winference.Infrastructure.Lemonade;
using Winference.Infrastructure.Npu;

namespace Winference.Tests;

public class ReplyStreamTests
{
    private static async Task<List<GenerationEvent>> ReadAsync(params string[] payloads)
    {
        var body = string.Concat(payloads.Select(payload => "data: " + payload + "\n\n")) + "data: [DONE]\n\n";
        var events = new List<GenerationEvent>();
        await foreach (var item in CompletionStreamReader.ReadAsync(new MemoryStream(Encoding.UTF8.GetBytes(body)))) events.Add(item);
        return events;
    }

    [Fact]
    public async Task Thinking_is_kept_apart_from_the_answer()
    {
        var events = await ReadAsync(
            """{"choices":[{"index":0,"delta":{"role":"assistant","reasoning_content":"Paris is the capital."}}]}""",
            """{"choices":[{"index":0,"delta":{"role":"assistant","content":"Paris."}}]}""",
            """{"choices":[{"finish_reason":"stop"}]}""");

        Assert.Equal("Paris is the capital.", Assert.Single(events, e => e.Kind == GenerationEventKind.ReasoningDelta).Text);
        Assert.Equal("Paris.", Assert.Single(events, e => e.Kind == GenerationEventKind.TextDelta).Text);
        Assert.Equal("stop", events[^1].FinishReason);
    }

    [Fact]
    public async Task An_engine_error_ending_is_reported_as_an_interrupted_reply()
    {
        var error = await Assert.ThrowsAsync<RuntimeException>(() => ReadAsync(
            """{"choices":[{"index":0,"delta":{"content":"Par"}}]}""",
            """{"choices":[{"finish_reason":"error"}]}"""));

        Assert.Equal("stream_error", error.Code);
    }

    [Theory]
    [InlineData(2048, 512)]
    [InlineData(4096, 512)]
    [InlineData(1024, 256)]
    public void Replies_always_keep_room_for_an_answer(int context, int reserve) =>
        Assert.Equal(reserve, RuntimeInstallation.ReplyReserve(context));

    [Fact]
    public async Task Only_a_reply_without_a_set_limit_may_use_the_remaining_context()
    {
        var backend = new FakeBackend();
        var broker = new RuntimeBroker(backend);
        var profile = TestProfiles.Create();
        await broker.StartAsync(profile);
        ChatMessage[] prompt = [new("user", "hi")];

        await foreach (var _ in broker.GenerateAsync(prompt)) { }
        Assert.True(backend.LastRequest!.UseRemainingContext);
        Assert.Equal(profile.MaxOutputTokens, backend.LastRequest.MaxOutputTokens);

        await foreach (var _ in broker.GenerateAsync(prompt, 100)) { }
        Assert.False(backend.LastRequest!.UseRemainingContext);
        Assert.Equal(100, backend.LastRequest.MaxOutputTokens);
    }
}

public class FastFlowLmLaunchTests
{
    [Fact]
    public void The_npu_engine_serves_only_the_chosen_model_on_this_pc()
    {
        var folder = Directory.CreateTempSubdirectory("winference-flm-");
        try
        {
            var manifest = Path.Combine(folder.FullName, "model.json");
            File.WriteAllText(manifest, """
                {"schemaVersion":1,"modelFamilyId":"llama3.2:1b","profileId":"p","artifactId":"sha256:a",
                 "providerModelId":"llama3.2-1b-FLM","checkpoint":"llama3.2:1b","contextLength":4096,"modelDirectory":"Llama-3.2-1B-NPU2"}
                """);
            var installation = new RuntimeInstallation(manifest, Path.Combine(folder.FullName, "flm.exe"), folder.FullName);
            var arguments = new FastFlowLmBackend(installation).LaunchArguments(4096);

            Assert.Equal(["serve", "llama3.2:1b"], arguments[..2]);
            Assert.Contains("--ctx-len", arguments);
            Assert.Equal("4096", arguments[Array.IndexOf(arguments, "--ctx-len") + 1]);
            Assert.Equal("127.0.0.1", arguments[Array.IndexOf(arguments, "--host") + 1]);
            Assert.Equal("0", arguments[Array.IndexOf(arguments, "--cors") + 1]);

            var environment = installation.CreateEnvironment();
            Assert.Equal(folder.FullName, environment["FLM_MODEL_PATH"]);
            Assert.DoesNotContain(environment.Keys, key => key.StartsWith("LEMONADE_", StringComparison.OrdinalIgnoreCase));
        }
        finally { folder.Delete(true); }
    }
}
