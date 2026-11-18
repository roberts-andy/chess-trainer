using System.Runtime.CompilerServices;
using Microsoft.Data.SqlClient;

[assembly: InternalsVisibleTo("ChessTrainer.Tests")]

namespace ChessTrainer.Web;

internal static class SqlConnectionRetry
{
    private static readonly TimeSpan Budget = TimeSpan.FromSeconds(100);

    internal static Task OpenAsync(SqlConnection connection, CancellationToken cancellationToken = default) =>
        OpenWithRetryAsync(connection.OpenAsync,
            error => error is SqlException sql && sql.Errors.Count > 0 && sql.Errors.Cast<SqlError>().All(item => IsTransient(item.Number)),
            Task.Delay, Budget, cancellationToken);

    internal static bool IsTransient(int number) =>
        number is 40613 or 40197 or 40501 or 10928 or 10929 or 49918 or 49919 or 49920 or -2;

    internal static async Task OpenWithRetryAsync(
        Func<CancellationToken, Task> open, Func<Exception, bool> isTransient,
        Func<TimeSpan, CancellationToken, Task> delay, TimeSpan budget, CancellationToken cancellationToken)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(budget);
        try
        {
            for (var attempt = 0; ; attempt++)
            {
                timeout.Token.ThrowIfCancellationRequested();
                try
                {
                    await open(timeout.Token);
                    return;
                }
                catch (Exception error) when (error is not OperationCanceledException && isTransient(error))
                {
                    await delay(TimeSpan.FromSeconds(attempt switch { 0 => 2, 1 => 4, 2 => 8, _ => 10 }), timeout.Token);
                }
            }
        }
        catch (OperationCanceledException) when (timeout.IsCancellationRequested && !cancellationToken.IsCancellationRequested)
        {
            throw new TimeoutException("Timed out connecting to the trainer database. Try again after it resumes.");
        }
    }
}
