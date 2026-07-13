using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace SurvivalcraftTravelMap.Teleport;

public enum TeleportExecutionStage
{
    ProtocolDispatch,
    ChunkLoad,
    CandidateSearch,
    MovementSnapshot,
    PositionWrite,
    PostMoveValidation,
    Rollback,
    PositionSync,
}

public sealed record TeleportFailureDiagnostic(
    TeleportExecutionStage Stage,
    Exception Exception);

internal readonly record struct TeleportRequestDiagnosticContext(
    string Route,
    uint? RequestId,
    string Kind);

internal static class TeleportDiagnosticContext
{
    private static readonly AsyncLocal<ScopeState?> Ambient = new();

    internal static TeleportRequestDiagnosticContext? Current => Ambient.Value?.Context;

    internal static bool HasReportedFailure => Ambient.Value?.HasReportedFailure ?? false;

    internal static IDisposable Ensure(TeleportRequestDiagnosticContext context)
    {
        var previous = Ambient.Value;
        if (previous?.Context == context)
        {
            return EmptyScope.Instance;
        }

        Ambient.Value = new ScopeState(context);
        return new RestoreScope(previous);
    }

    internal static void MarkFailureReported()
    {
        if (Ambient.Value is { } state)
        {
            state.HasReportedFailure = true;
        }
    }

    private sealed class ScopeState(TeleportRequestDiagnosticContext context)
    {
        internal TeleportRequestDiagnosticContext Context { get; } = context;

        internal bool HasReportedFailure { get; set; }
    }

    private sealed class RestoreScope(ScopeState? previous) : IDisposable
    {
        private ScopeState? _previous = previous;
        private bool _disposed;

        public void Dispose()
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            Ambient.Value = _previous;
            _previous = null;
        }
    }

    private sealed class EmptyScope : IDisposable
    {
        internal static EmptyScope Instance { get; } = new();

        public void Dispose()
        {
        }
    }
}

internal static partial class TeleportDiagnosticReporter
{
    internal static void Report(TeleportFailureDiagnostic diagnostic)
    {
        ArgumentNullException.ThrowIfNull(diagnostic);
        try
        {
            Engine.Log.Warning(FormatFailure(TeleportDiagnosticContext.Current, diagnostic));
        }
        catch
        {
            // Diagnostics must not replace the teleport failure being reported.
        }
        finally
        {
            TeleportDiagnosticContext.MarkFailureReported();
        }
    }

    internal static string FormatFailure(
        TeleportRequestDiagnosticContext? context,
        TeleportFailureDiagnostic diagnostic)
    {
        ArgumentNullException.ThrowIfNull(diagnostic);
        var builder = new StringBuilder("[TravelMap] Teleport failure route=")
            .Append(context?.Route ?? "none")
            .Append(", request=")
            .Append(context?.RequestId?.ToString(CultureInfo.InvariantCulture) ?? "none")
            .Append(", kind=")
            .Append(context?.Kind ?? "none")
            .Append(", stage=")
            .Append(diagnostic.Stage)
            .Append('.');
        AppendException(builder, diagnostic.Exception, "exception");
        return builder.ToString();
    }

    private static void AppendException(StringBuilder builder, Exception exception, string label)
    {
        builder.Append(' ')
            .Append(label)
            .Append("-type=")
            .Append(exception.GetType().FullName ?? exception.GetType().Name)
            .Append(", message=")
            .Append(RedactNumbers(exception.Message))
            .Append(", stack=")
            .Append(RedactStackTrace(exception.StackTrace ?? "none"))
            .Append('.');

        if (exception is AggregateException aggregate)
        {
            foreach (var inner in aggregate.InnerExceptions)
            {
                AppendException(builder, inner, "inner-exception");
            }
        }
        else if (exception.InnerException is { } inner)
        {
            AppendException(builder, inner, "inner-exception");
        }
    }

    private static string RedactNumbers(string value) => NumberLiteral().Replace(value, "<number>");

    private static string RedactStackTrace(string stackTrace)
    {
        var builder = new StringBuilder(stackTrace.Length);
        var lineStart = 0;
        while (lineStart < stackTrace.Length)
        {
            var lineEnd = lineStart;
            while (lineEnd < stackTrace.Length && stackTrace[lineEnd] is not ('\r' or '\n'))
            {
                lineEnd++;
            }

            AppendRedactedStackLine(builder, stackTrace[lineStart..lineEnd]);
            while (lineEnd < stackTrace.Length && stackTrace[lineEnd] is '\r' or '\n')
            {
                builder.Append(stackTrace[lineEnd++]);
            }

            lineStart = lineEnd;
        }

        return builder.ToString();
    }

    private static void AppendRedactedStackLine(StringBuilder builder, string line)
    {
        if (line.AsSpan().TrimStart().StartsWith("at ", StringComparison.Ordinal))
        {
            var sourceIndex = line.IndexOf(" in ", StringComparison.Ordinal);
            if (sourceIndex < 0)
            {
                builder.Append(line);
                return;
            }

            var sourceStart = sourceIndex + " in ".Length;
            builder.Append(line.AsSpan(0, sourceStart));
            builder.Append(RedactNumbers(line[sourceStart..]));
            return;
        }

        builder.Append(RedactNumbers(line));
    }

    [GeneratedRegex(
        @"[+-]?(?:\d+(?:\.\d*)?|\.\d+)(?:[eE][+-]?\d+)?",
        RegexOptions.CultureInvariant)]
    private static partial Regex NumberLiteral();
}
