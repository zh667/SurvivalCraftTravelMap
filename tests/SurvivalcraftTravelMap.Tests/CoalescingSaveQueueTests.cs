using SurvivalcraftTravelMap.UI;
using Xunit;

namespace SurvivalcraftTravelMap.Tests;

public sealed class CoalescingSaveQueueTests
{
    [Fact]
    public async Task Rapid_requests_debounce_to_one_save_of_the_latest_state()
    {
        var value = 0;
        var saved = new List<int>();
        using var queue = new CoalescingSaveQueue(
            _ =>
            {
                saved.Add(value);
                return Task.CompletedTask;
            },
            _ => { },
            TimeSpan.FromMilliseconds(40));

        value = 1;
        queue.RequestSave();
        value = 2;
        queue.RequestSave();
        value = 3;
        queue.RequestSave();
        await queue.WhenIdleAsync(TestContext.Current.CancellationToken);

        Assert.Equal([3], saved);
    }

    [Fact]
    public async Task Requests_during_a_save_coalesce_to_one_follow_up_with_latest_state()
    {
        var value = 1;
        var saved = new List<int>();
        var firstStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseFirst = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var queue = new CoalescingSaveQueue(
            async cancellationToken =>
            {
                saved.Add(value);
                if (saved.Count == 1)
                {
                    firstStarted.SetResult();
                    await releaseFirst.Task.WaitAsync(cancellationToken);
                }
            },
            _ => { },
            TimeSpan.Zero);

        queue.RequestSave();
        await firstStarted.Task.WaitAsync(TestContext.Current.CancellationToken);
        value = 2;
        queue.RequestSave();
        value = 3;
        queue.RequestSave();
        releaseFirst.SetResult();
        await queue.WhenIdleAsync(TestContext.Current.CancellationToken);

        Assert.Equal([1, 3], saved);
    }

    [Fact]
    public async Task Failure_is_observed_reported_and_queue_becomes_idle()
    {
        var reported = new TaskCompletionSource<Exception>(TaskCreationOptions.RunContinuationsAsynchronously);
        using var queue = new CoalescingSaveQueue(
            _ => throw new IOException("save failed"),
            error => reported.TrySetResult(error),
            TimeSpan.Zero);

        queue.RequestSave();

        Assert.IsType<IOException>(await reported.Task.WaitAsync(TestContext.Current.CancellationToken));
        await queue.WhenIdleAsync(TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task Dispose_cancels_pending_debounce_and_observes_worker_completion()
    {
        var saves = 0;
        var queue = new CoalescingSaveQueue(
            _ =>
            {
                saves++;
                return Task.CompletedTask;
            },
            _ => { },
            TimeSpan.FromMinutes(1));
        queue.RequestSave();

        queue.Dispose();
        await queue.WhenIdleAsync(TestContext.Current.CancellationToken);

        Assert.Equal(0, saves);
    }
}
