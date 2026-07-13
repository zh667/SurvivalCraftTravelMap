using System.Runtime.CompilerServices;
using SurvivalcraftTravelMap.Teleport;
using Xunit;

namespace SurvivalcraftTravelMap.Tests;

public sealed class TeleportDiagnosticReporterTests
{
    [Fact]
    public void Formatter_redacts_invariant_numbers_from_outer_and_inner_exception_details()
    {
        var exception = CaptureNestedException();
        var context = new TeleportRequestDiagnosticContext("remote", 73, "WaypointRequest");

        var formatted = TeleportDiagnosticReporter.FormatFailure(
            context,
            new TeleportFailureDiagnostic(TeleportExecutionStage.PositionWrite, exception));

        Assert.Contains("route=remote", formatted, StringComparison.Ordinal);
        Assert.Contains("request=73", formatted, StringComparison.Ordinal);
        Assert.Contains("kind=WaypointRequest", formatted, StringComparison.Ordinal);
        Assert.Contains("stage=PositionWrite", formatted, StringComparison.Ordinal);
        Assert.Contains(typeof(InvalidOperationException).FullName!, formatted, StringComparison.Ordinal);
        Assert.Contains(typeof(ArgumentException).FullName!, formatted, StringComparison.Ordinal);
        Assert.Contains(nameof(ThrowInnerCoordinateException), formatted, StringComparison.Ordinal);
        Assert.Contains("<number>", formatted, StringComparison.Ordinal);
        foreach (var sensitiveNumber in new[]
                 {
                     "123456789",
                     "-987654321",
                     "+42",
                     "12.375",
                     "-6.02e+23",
                     "314159",
                     "-271828",
                     "0.000125",
                     "+9.5E-7",
                 })
        {
            Assert.DoesNotContain(sensitiveNumber, formatted, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void Formatter_uses_none_for_absent_request_context()
    {
        var formatted = TeleportDiagnosticReporter.FormatFailure(
            null,
            new TeleportFailureDiagnostic(
                TeleportExecutionStage.ProtocolDispatch,
                new InvalidOperationException("failure 88")));

        Assert.Contains("route=none", formatted, StringComparison.Ordinal);
        Assert.Contains("request=none", formatted, StringComparison.Ordinal);
        Assert.Contains("kind=none", formatted, StringComparison.Ordinal);
        Assert.DoesNotContain("88", formatted, StringComparison.Ordinal);
    }

    [Fact]
    public void Matching_nested_scopes_share_the_reported_flag()
    {
        var context = new TeleportRequestDiagnosticContext("remote", 11, "SurfaceRequest");

        using (TeleportDiagnosticContext.Ensure(context))
        {
            Assert.False(TeleportDiagnosticContext.HasReportedFailure);
            using (TeleportDiagnosticContext.Ensure(context))
            {
                TeleportDiagnosticContext.MarkFailureReported();
            }

            Assert.Equal(context, TeleportDiagnosticContext.Current);
            Assert.True(TeleportDiagnosticContext.HasReportedFailure);
        }

        Assert.Null(TeleportDiagnosticContext.Current);
        Assert.False(TeleportDiagnosticContext.HasReportedFailure);
    }

    [Fact]
    public void Unrelated_nested_scope_restores_the_previous_context_and_reported_flag()
    {
        var outer = new TeleportRequestDiagnosticContext("local", null, "WaypointRequest");
        var inner = new TeleportRequestDiagnosticContext("invitation", null, "Teleport");

        using (TeleportDiagnosticContext.Ensure(outer))
        {
            TeleportDiagnosticContext.MarkFailureReported();
            using (TeleportDiagnosticContext.Ensure(inner))
            {
                Assert.Equal(inner, TeleportDiagnosticContext.Current);
                Assert.False(TeleportDiagnosticContext.HasReportedFailure);
                TeleportDiagnosticContext.MarkFailureReported();
            }

            Assert.Equal(outer, TeleportDiagnosticContext.Current);
            Assert.True(TeleportDiagnosticContext.HasReportedFailure);
        }
    }

    [Fact]
    public async Task Parallel_async_flows_do_not_leak_request_context()
    {
        var first = new TeleportRequestDiagnosticContext("remote", 101, "SurfaceRequest");
        var second = new TeleportRequestDiagnosticContext("host", 202, "WaypointRequest");
        var ready = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var arrivals = 0;

        async Task<(TeleportRequestDiagnosticContext? Context, bool Reported)> RunAsync(
            TeleportRequestDiagnosticContext context)
        {
            using var scope = TeleportDiagnosticContext.Ensure(context);
            if (Interlocked.Increment(ref arrivals) == 2)
            {
                ready.SetResult();
            }

            await ready.Task;
            if (context == first)
            {
                TeleportDiagnosticContext.MarkFailureReported();
            }

            await Task.Yield();
            return (TeleportDiagnosticContext.Current, TeleportDiagnosticContext.HasReportedFailure);
        }

        var results = await Task.WhenAll(Task.Run(() => RunAsync(first)), Task.Run(() => RunAsync(second)));

        Assert.Equal(first, results[0].Context);
        Assert.True(results[0].Reported);
        Assert.Equal(second, results[1].Context);
        Assert.False(results[1].Reported);
        Assert.Null(TeleportDiagnosticContext.Current);
    }

    private static Exception CaptureNestedException()
    {
        try
        {
            ThrowInnerCoordinateException();
        }
        catch (Exception inner)
        {
            try
            {
                throw new InvalidOperationException(
                    "outer target 123456789 -987654321 +42 12.375 -6.02e+23",
                    inner);
            }
            catch (Exception outer)
            {
                return outer;
            }
        }

        throw new InvalidOperationException("Unreachable.");
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void ThrowInnerCoordinateException() =>
        throw new ArgumentException("inner target 314159 -271828 0.000125 +9.5E-7");
}
