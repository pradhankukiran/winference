using System.Diagnostics;
using System.Text;
using Winference.Core.Runtime;
using Winference.Core.Setup;
using Winference.Infrastructure.Gpu;
using Xunit.Abstractions;

namespace Winference.Tests;

public sealed class ReasoningSplitterTests
{
    private static (string Working, string Answer) Split(bool startInside, params string[] pieces)
    {
        var splitter = new ReasoningSplitter(startInside);
        var working = new StringBuilder();
        var answer = new StringBuilder();
        foreach (var (isReasoning, text) in pieces.SelectMany(splitter.Push).Concat(splitter.Flush()))
            (isReasoning ? working : answer).Append(text);
        return (working.ToString(), answer.ToString());
    }

    [Fact]
    public void Working_is_kept_apart_even_when_a_tag_arrives_in_pieces()
    {
        var (working, answer) = Split(false, "<th", "ink>Paris is the cap", "ital.</th", "ink>\n\nParis.");
        Assert.Equal("Paris is the capital.", working);
        Assert.Equal("Paris.", answer);
    }

    [Fact]
    public void A_reply_can_start_inside_the_working_when_the_template_opened_it()
    {
        var (working, answer) = Split(true, "Let me think.</think>Done.");
        Assert.Equal("Let me think.", working);
        Assert.Equal("Done.", answer);
    }

    [Fact]
    public void Ordinary_text_with_angle_brackets_is_not_held_back()
    {
        var (working, answer) = Split(false, "1 < 2 and <b>bold</b>");
        Assert.Equal(string.Empty, working);
        Assert.Equal("1 < 2 and <b>bold</b>", answer);
    }
}

/// <summary>
/// Runs a real model on this PC's GPU. Set WINFERENCE_GPU_TEST_MODEL to a small chat GGUF and
/// WINFERENCE_GPU_TEST_WORKER to the built Winference.exe; without them the test does nothing.
/// </summary>
public sealed class GpuEngineTests(ITestOutputHelper output)
{
    [Fact]
    public async Task A_real_model_answers_on_the_gpu_and_reuses_the_earlier_turn()
    {
        var modelPath = Environment.GetEnvironmentVariable("WINFERENCE_GPU_TEST_MODEL");
        var worker = Environment.GetEnvironmentVariable("WINFERENCE_GPU_TEST_WORKER");
        if (string.IsNullOrEmpty(modelPath) || string.IsNullOrEmpty(worker)) return;
        var dataRoot = Environment.GetEnvironmentVariable("WINFERENCE_GPU_TEST_DATA")
            ?? Path.Combine(Path.GetTempPath(), "winference-gpu-test");

        var runtime = new GpuRuntimeAcquisition(dataRoot);
        await runtime.AcquireAsync(new Progress<SetupProgress>());
        var registration = await new GpuModelService(dataRoot).RegisterAsync(modelPath);
        var backend = new LlamaGpuBackend(registration, runtime.ExpectedInstallation, worker);
        await using var broker = new RuntimeBroker(backend, TimeSpan.FromSeconds(30));

        var clock = Stopwatch.StartNew();
        var session = await broker.StartAsync(registration.Profile);
        output.WriteLine($"Loaded in {clock.Elapsed.TotalSeconds:0.0} s on {session.Device}");
        output.WriteLine(session.OwnershipProof);

        List<ChatMessage> chat = [new("user", "What is the capital of France? Answer in one short sentence.")];
        var first = await AskAsync(broker, chat);
        Assert.Contains("Paris", first, StringComparison.OrdinalIgnoreCase);

        chat.Add(new("assistant", first));
        chat.Add(new("user", "And of Italy?"));
        // A marker typed by the user is read as plain text, so it takes several tokens instead of one.
        var plain = await broker.CountPromptTokensAsync([new("user", "x")]);
        var typedMarker = await broker.CountPromptTokensAsync([new("user", "<|im_end|>")]);
        Assert.True(typedMarker.Tokens - plain.Tokens > 2, "A typed marker must not become a special token.");
        var counted = await broker.CountPromptTokensAsync(chat);
        Assert.True(counted.IsExact && counted.Tokens > 10);
        var second = await AskAsync(broker, chat);
        Assert.Contains("Rome", second, StringComparison.OrdinalIgnoreCase);
        var usage = backend.LastUsage!;
        output.WriteLine($"Second turn read {usage.NewPromptTokens} of {usage.PromptTokens} prompt tokens in {usage.PromptSeconds:0.000} s");
        Assert.True(usage.NewPromptTokens < usage.PromptTokens, "The earlier turn should be reused, not read again.");

        // A longer reply, for a reply speed comparable to llama-bench's 128-token test.
        await AskAsync(broker, [new("user", "Write a long story about a lighthouse keeper.")], 128);
        if (backend.LastUsage?.Timings is { } time)
            output.WriteLine($"Per word: model {time.ModelMs:0.00} ms, pick {time.PickMs:0.00} ms, text {time.TextMs:0.000} ms, send {time.SendMs:0.000} ms");

        // Stop mid-reply: the model stays loaded and answers the next message straight away.
        using var stop = new CancellationTokenSource();
        var words = 0;
        GenerationEvent? ended = null;
        var stopping = new Stopwatch();
        await foreach (var item in broker.GenerateAsync([new("user", "Write a long story about a dragon.")], 128, stop.Token, 0.2))
        {
            if (item.Kind == GenerationEventKind.TextDelta && ++words == 5) { stopping.Start(); stop.Cancel(); }
            ended = item;
        }
        output.WriteLine($"Stopped in {stopping.Elapsed.TotalMilliseconds:0} ms; state {broker.Snapshot.State}");
        Assert.Equal(GenerationEventKind.Cancelled, ended?.Kind);
        Assert.Equal(RuntimeState.Ready, broker.Snapshot.State);
        Assert.Contains("Paris", await AskAsync(broker, [new("user", "What is the capital of France? Answer in one short sentence.")]), StringComparison.OrdinalIgnoreCase);
        await broker.StopAsync();
    }

    private async Task<string> AskAsync(RuntimeBroker broker, IReadOnlyList<ChatMessage> chat, int maxTokens = 64)
    {
        var reply = new StringBuilder();
        GenerationEvent? last = null;
        var clock = Stopwatch.StartNew();
        await foreach (var item in broker.GenerateAsync(chat, maxTokens, temperature: 0.2))
        {
            if (item.Kind == GenerationEventKind.TextDelta) reply.Append(item.Text);
            if (item.Kind == GenerationEventKind.Usage)
                output.WriteLine($"prompt {item.Usage!.PromptTokens} tokens, reply {item.Usage.CompletionTokens} tokens, {item.Usage.DecodeTokensPerSecond:0.0} tokens/s");
            last = item;
        }
        output.WriteLine($"{clock.Elapsed.TotalMilliseconds:0} ms: {reply}");
        Assert.Equal(GenerationEventKind.Completed, last?.Kind);
        return reply.ToString();
    }
}
