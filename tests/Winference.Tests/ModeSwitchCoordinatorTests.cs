using Winference.Core.Runtime;

namespace Winference.Tests;

public class ModeSwitchCoordinatorTests
{
    private sealed class Plan(string id)
    {
        public int Checks, Loads, Stops;
        public Exception? CheckFailure, LoadFailure, StopFailure;
        public Core.Models.ModelProfile Profile { get; } = TestProfiles.Create(id);

        public ModelActivationPlan Build() => new(Profile,
            _ => { ++Checks; return CheckFailure is null ? Task.CompletedTask : Task.FromException(CheckFailure); },
            _ =>
            {
                ++Loads;
                return LoadFailure is null ? Task.FromResult(TestProfiles.Session(Profile)) : Task.FromException<RuntimeSession>(LoadFailure);
            },
            _ => { ++Stops; return StopFailure is null ? Task.CompletedTask : Task.FromException(StopFailure); });
    }

    [Fact]
    public async Task Switching_stops_the_previous_model_before_loading_the_next()
    {
        var previous = new Plan("a");
        var target = new Plan("b");
        var result = await new ModeSwitchCoordinator().SwitchAsync(target.Build(), previous.Build(), restorePrevious: true);

        Assert.Equal(ModeSwitchStage.Ready, result.Stage);
        Assert.Equal(1, previous.Stops);
        Assert.Equal(1, target.Loads);
        Assert.Equal(0, previous.Loads);
    }

    [Fact]
    public async Task A_failed_check_never_touches_the_running_model()
    {
        var previous = new Plan("a");
        var target = new Plan("b") { CheckFailure = new RuntimeException("bad", "Not usable.") };
        var result = await new ModeSwitchCoordinator().SwitchAsync(target.Build(), previous.Build(), restorePrevious: true);

        Assert.Equal(ModeSwitchStage.Failed, result.Stage);
        Assert.Equal(ModeSwitchRecovery.PreviousPreserved, result.Recovery);
        Assert.Equal(0, previous.Stops);
        Assert.Contains("Not usable.", result.Detail);
    }

    [Fact]
    public async Task A_failed_load_restores_the_previous_model()
    {
        var previous = new Plan("a");
        var target = new Plan("b") { LoadFailure = new RuntimeException("bad", "Could not load.") };
        var result = await new ModeSwitchCoordinator().SwitchAsync(target.Build(), previous.Build(), restorePrevious: true);

        Assert.Equal(ModeSwitchRecovery.PreviousRestored, result.Recovery);
        Assert.Equal(1, previous.Loads);
        Assert.Equal(previous.Profile, result.ActiveProfile);
    }

    [Fact]
    public async Task When_the_previous_model_cannot_come_back_the_app_says_it_stopped()
    {
        var previous = new Plan("a") { LoadFailure = new RuntimeException("bad", "Gone.") };
        var target = new Plan("b") { LoadFailure = new RuntimeException("bad", "Could not load.") };
        var result = await new ModeSwitchCoordinator().SwitchAsync(target.Build(), previous.Build(), restorePrevious: true);

        Assert.Equal(ModeSwitchRecovery.Stopped, result.Recovery);
        Assert.Null(result.ActiveProfile);
    }

    [Fact]
    public async Task A_model_that_will_not_stop_blocks_loading_another_on_top_of_it()
    {
        var switches = new ModeSwitchCoordinator();
        var previous = new Plan("a") { StopFailure = new IOException("stuck") };
        var target = new Plan("b");

        var first = await switches.SwitchAsync(target.Build(), previous.Build(), restorePrevious: true);
        var second = await switches.SwitchAsync(target.Build(), null, restorePrevious: false);

        Assert.Equal(ModeSwitchRecovery.CleanupUnconfirmed, first.Recovery);
        Assert.Equal(ModeSwitchRecovery.CleanupUnconfirmed, second.Recovery);
        Assert.Equal(0, target.Loads);
    }
}
