using System.Diagnostics;
using SurvivalcraftTravelMap.Mod;
using Xunit;

namespace SurvivalcraftTravelMap.Tests;

public sealed class BoundedTaskObserverTests
{
    [Fact]
    public async Task Timeout_returns_promptly_and_observes_a_late_fault()
    {
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var reported = new TaskCompletionSource<Exception>(TaskCreationOptions.RunContinuationsAsynchronously);
        var stopwatch = Stopwatch.StartNew();

        var completed = BoundedTaskObserver.ObserveWithin(
            completion.Task,
            TimeSpan.FromMilliseconds(40),
            error => reported.TrySetResult(error));

        Assert.False(completed);
        Assert.True(stopwatch.Elapsed < TimeSpan.FromMilliseconds(500));
        completion.SetException(new IOException("late failure"));
        Assert.IsType<IOException>(await reported.Task.WaitAsync(TestContext.Current.CancellationToken));
    }

    [Fact]
    public void Completed_fault_is_observed_and_reported()
    {
        Exception? reported = null;

        var completed = BoundedTaskObserver.ObserveWithin(
            Task.FromException(new InvalidOperationException("failed")),
            TimeSpan.FromSeconds(1),
            error => reported = error);

        Assert.True(completed);
        Assert.IsType<InvalidOperationException>(reported);
    }
}
