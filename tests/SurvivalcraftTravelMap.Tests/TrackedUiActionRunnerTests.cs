using SurvivalcraftTravelMap.UI;
using Xunit;

namespace SurvivalcraftTravelMap.Tests;

public sealed class TrackedUiActionRunnerTests
{
    [Fact]
    public async Task Rejects_a_second_action_until_the_first_finishes()
    {
        using var runner = new TrackedUiActionRunner(_ => { });
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        Assert.True(runner.TryRun(_ => release.Task));
        Assert.False(runner.TryRun(_ => Task.CompletedTask));

        release.SetResult();
        await runner.WhenIdleAsync(TestContext.Current.CancellationToken);

        Assert.True(runner.TryRun(_ => Task.CompletedTask));
        await runner.WhenIdleAsync(TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task Observes_and_reports_action_failures()
    {
        var reported = new TaskCompletionSource<Exception>(TaskCreationOptions.RunContinuationsAsynchronously);
        using var runner = new TrackedUiActionRunner(error => reported.TrySetResult(error));

        Assert.True(runner.TryRun(_ => throw new IOException("disk failed")));

        var error = await reported.Task.WaitAsync(TestContext.Current.CancellationToken);
        await runner.WhenIdleAsync(TestContext.Current.CancellationToken);
        Assert.IsType<IOException>(error);
    }

    [Fact]
    public async Task Dispose_cancels_cooperative_action_and_observes_completion()
    {
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var runner = new TrackedUiActionRunner(_ => { });
        Assert.True(runner.TryRun(async cancellationToken =>
        {
            started.SetResult();
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
        }));
        await started.Task.WaitAsync(TestContext.Current.CancellationToken);

        runner.Dispose();
        await runner.WhenIdleAsync(TestContext.Current.CancellationToken);

        Assert.False(runner.TryRun(_ => Task.CompletedTask));
    }
}
