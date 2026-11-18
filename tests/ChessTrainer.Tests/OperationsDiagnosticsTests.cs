using ChessTrainer.Web;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace ChessTrainer.Tests;

public sealed class OperationsDiagnosticsTests
{
    [Fact]
    public void FrameworkErrorsDoNotEmitMessagesExceptionsScopesOrArbitraryState()
    {
        var entry = new LogEntry<string>(LogLevel.Error, "Microsoft.AspNetCore.Diagnostics.ExceptionHandlerMiddleware",
            new EventId(1), "private-fen-note-token", new IOException("private-upstream-body"),
            (_, _) => throw new InvalidOperationException("The formatter must not run."));
        using var output = new StringWriter();
        new PrivacySafeConsoleFormatter().Write(entry, null, output);
        Assert.Contains("\"module\":\"aspnet\"", output.ToString());
        Assert.DoesNotContain("private", output.ToString());
    }

    [Fact]
    public void OperationEventsKeepOnlyValidatedEnumsAndOpaqueCorrelation()
    {
        KeyValuePair<string, object?>[] state =
        [
            new("Operation", TrainerOperation.Vision),
            new("FailureCategory", TrainerFailureCategory.ExternalService),
            new("Correlation", new string('a', 32)),
            new("OwnerId", "private-owner"),
            new("FEN", "private-fen"),
            new("Notes", "private-note")
        ];
        var entry = new LogEntry<KeyValuePair<string, object?>[]>(LogLevel.Warning, "ChessTrainer.Web.Home",
            new EventId(OperationDiagnostics.FailureEventId), state, new Exception("private-body"), (_, _) => "private-text");
        using var output = new StringWriter();
        new PrivacySafeConsoleFormatter().Write(entry, null, output);
        Assert.Contains("\"operation\":\"Vision\"", output.ToString());
        Assert.Contains("\"failureCategory\":\"ExternalService\"", output.ToString());
        Assert.Contains(new string('a', 32), output.ToString());
        Assert.DoesNotContain("private", output.ToString());
    }

    [Fact]
    public void StringsCannotMasqueradeAsWhitelistedOperationFields()
    {
        KeyValuePair<string, object?>[] state =
        [
            new("Operation", "private-data"),
            new("FailureCategory", "private-data"),
            new("Correlation", "private-data")
        ];
        var entry = new LogEntry<KeyValuePair<string, object?>[]>(LogLevel.Error, "private-category",
            new EventId(OperationDiagnostics.FailureEventId), state, null, (_, _) => "private");
        using var output = new StringWriter();
        new PrivacySafeConsoleFormatter().Write(entry, null, output);
        Assert.DoesNotContain("private", output.ToString());
    }
}
