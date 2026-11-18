using System.Data.Common;
using System.Diagnostics;
using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Logging.Console;

namespace ChessTrainer.Web;

public enum TrainerOperation { Attempt, Study, Vision, Request, Startup }
public enum TrainerFailureCategory { Database, Timeout, Authorization, ExternalService, Storage, Cancelled, Unexpected }

public static class OperationDiagnostics
{
    public const int FailureEventId = 7401;

    public static void LogFailure(ILogger logger, TrainerOperation operation, Exception error)
    {
        var category = error switch
        {
            DbException => TrainerFailureCategory.Database,
            TimeoutException => TrainerFailureCategory.Timeout,
            UnauthorizedAccessException => TrainerFailureCategory.Authorization,
            HttpRequestException => TrainerFailureCategory.ExternalService,
            IOException => TrainerFailureCategory.Storage,
            OperationCanceledException => TrainerFailureCategory.Cancelled,
            _ => TrainerFailureCategory.Unexpected
        };
        logger.Log(category == TrainerFailureCategory.Cancelled ? LogLevel.Information : LogLevel.Warning,
            new EventId(FailureEventId, "OperationFailure"),
            "Operation {Operation} failed. Category {FailureCategory}; correlation {Correlation}",
            operation, category, Correlation());
    }

    public static ILoggingBuilder AddPrivacySafeConsole(this ILoggingBuilder builder) =>
        builder.AddConsole(options => options.FormatterName = PrivacySafeConsoleFormatter.FormatterName)
            .AddConsoleFormatter<PrivacySafeConsoleFormatter, ConsoleFormatterOptions>();

    public static IApplicationBuilder UsePrivacySafeDiagnostics(this IApplicationBuilder app) =>
        app.UseMiddleware<PrivacySafeRequestDiagnostics>();

    internal static string Correlation() => Activity.Current?.TraceId.ToHexString() ?? Guid.NewGuid().ToString("N");
}

public sealed class PrivacySafeRequestDiagnostics(RequestDelegate next, ILogger<PrivacySafeRequestDiagnostics> logger)
{
    public async Task InvokeAsync(HttpContext context)
    {
        try
        {
            await next(context);
            if (context.Response.StatusCode >= 500)
                OperationDiagnostics.LogFailure(logger, TrainerOperation.Request, new InvalidOperationException());
        }
        catch (Exception error)
        {
            OperationDiagnostics.LogFailure(logger, TrainerOperation.Request, error);
            throw;
        }
    }
}

public sealed class PrivacySafeConsoleFormatter() : ConsoleFormatter(FormatterName)
{
    public const string FormatterName = "chess-trainer-safe";

    public override void Write<TState>(in LogEntry<TState> entry, IExternalScopeProvider? scopeProvider, TextWriter writer)
    {
        var record = new Dictionary<string, object?>
        {
            ["timestamp"] = DateTimeOffset.UtcNow,
            ["level"] = entry.LogLevel.ToString(),
            ["module"] = entry.Category.StartsWith("ChessTrainer.", StringComparison.Ordinal) ? "trainer" :
                entry.Category.StartsWith("Microsoft.AspNetCore.", StringComparison.Ordinal) ? "aspnet" : "runtime",
            ["eventId"] = entry.EventId.Id
        };
        if (entry.EventId.Id == OperationDiagnostics.FailureEventId &&
            entry.State is IEnumerable<KeyValuePair<string, object?>> values)
        {
            foreach (var (key, value) in values)
            {
                if (key == "Operation" && value is TrainerOperation operation && Enum.IsDefined(operation))
                    record["operation"] = operation.ToString();
                else if (key == "FailureCategory" && value is TrainerFailureCategory category && Enum.IsDefined(category))
                    record["failureCategory"] = category.ToString();
                else if (key == "Correlation" && value is string correlation &&
                    correlation.Length == 32 && correlation.All(Uri.IsHexDigit))
                    record["correlation"] = correlation;
            }
        }
        // Never invoke the original formatter or serialize exceptions, scopes, or arbitrary state.
        writer.WriteLine(JsonSerializer.Serialize(record));
    }
}
