using System.Text;
using Winference.Core.Runtime;
using Winference.Infrastructure.Lemonade;

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
