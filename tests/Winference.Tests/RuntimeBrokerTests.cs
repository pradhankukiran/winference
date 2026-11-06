using Winference.Core.Runtime;

namespace Winference.Tests;

public class RuntimeBrokerTests
{
    private static readonly ChatMessage[] Prompt = [new("user", "hi")];

    [Fact]
    public async Task A_finished_reply_leaves_the_model_ready()
    {
        var backend = new FakeBackend();
        var broker = new RuntimeBroker(backend);
        await broker.StartAsync(TestProfiles.Create());

        var kinds = new List<GenerationEventKind>();
        await foreach (var item in broker.GenerateAsync(Prompt)) kinds.Add(item.Kind);

        Assert.Equal(GenerationEventKind.Completed, kinds[^1]);
        Assert.Equal(RuntimeState.Ready, broker.Snapshot.State);
    }

    [Fact]
    public async Task Stopping_a_reply_unloads_and_the_same_broker_can_load_again()
    {
        var backend = new FakeBackend { HoldReply = true };
        var profile = TestProfiles.Create();
        var broker = new RuntimeBroker(backend, TimeSpan.FromSeconds(5));
        await broker.StartAsync(profile);

        using var cancel = new CancellationTokenSource();
        var reply = Task.Run(async () =>
        {
            GenerationEvent? last = null;
            await foreach (var item in broker.GenerateAsync(Prompt, cancellationToken: cancel.Token)) last = item;
            return last;
        });
        await backend.ReplyStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
        cancel.Cancel();
        await reply.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Equal(RuntimeState.Idle, broker.Snapshot.State);
        Assert.True(backend.Stops >= 1);

        backend.HoldReply = false;
        await broker.StartAsync(profile);
        Assert.Equal(RuntimeState.Ready, broker.Snapshot.State);
        Assert.Equal(2, backend.Starts);
    }

    [Fact]
    public async Task A_crash_during_a_reply_is_reported_and_not_shown_as_ready()
    {
        var backend = new FakeBackend { GenerateFailure = new RuntimeException("crash", "The runtime stopped.") };
        var broker = new RuntimeBroker(backend, TimeSpan.FromSeconds(5));
        await broker.StartAsync(TestProfiles.Create());

        GenerationEvent? last = null;
        await foreach (var item in broker.GenerateAsync(Prompt)) last = item;

        Assert.Equal(GenerationEventKind.Failed, last?.Kind);
        Assert.Equal("The runtime stopped.", last?.Error);
        Assert.Equal(RuntimeState.Error, broker.Snapshot.State);
    }

    [Fact]
    public async Task A_failed_load_cleans_up_and_can_be_retried()
    {
        var backend = new FakeBackend { StartFailure = new RuntimeException("no", "No device.") };
        var profile = TestProfiles.Create();
        var broker = new RuntimeBroker(backend, TimeSpan.FromSeconds(5));

        await Assert.ThrowsAsync<RuntimeException>(() => broker.StartAsync(profile));
        Assert.Equal(RuntimeState.Error, broker.Snapshot.State);
        Assert.True(backend.Stops >= 1);

        backend.StartFailure = null;
        await broker.StartAsync(profile);
        Assert.Equal(RuntimeState.Ready, broker.Snapshot.State);
    }
}
