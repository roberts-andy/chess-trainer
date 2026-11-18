using ChessTrainer.Web;
using Xunit;

namespace ChessTrainer.Tests.Infrastructure;

public sealed class SqlConnectionRetryTests
{
    [Theory]
    [InlineData(40613, true)]
    [InlineData(40501, true)]
    [InlineData(18456, false)]
    [InlineData(208, false)]
    [InlineData(1205, false)]
    public void RetriesOnlySelectedConnectionFailures(int sqlNumber, bool expected) =>
        Assert.Equal(expected, SqlConnectionRetry.IsTransient(sqlNumber));

    [Fact]
    public async Task ResumedConnectionIsReturnedAfterTransientFailures()
    {
        int opens = 0;
        var delays = new List<TimeSpan>();
        await SqlConnectionRetry.OpenWithRetryAsync(
            _ => ++opens < 3 ? Task.FromException(new IOException()) : Task.CompletedTask,
            error => error is IOException,
            (wait, _) => { delays.Add(wait); return Task.CompletedTask; },
            TimeSpan.FromSeconds(5), default);

        Assert.Equal(3, opens);
        Assert.Equal(new[] { TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(4) }, delays);
    }

    [Fact]
    public async Task PermanentFailuresDoNotEnterTheBackoff()
    {
        var failure = new InvalidOperationException();
        var thrown = await Assert.ThrowsAsync<InvalidOperationException>(() => SqlConnectionRetry.OpenWithRetryAsync(
            _ => Task.FromException(failure), _ => false,
            (_, _) => throw new Exception("Unexpected delay."), TimeSpan.FromSeconds(5), default));
        Assert.Same(failure, thrown);
    }

    [Fact]
    public async Task CallerCancellationDuringBackoffPreventsAnotherOpen()
    {
        using var cancellation = new CancellationTokenSource();
        int opens = 0;
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => SqlConnectionRetry.OpenWithRetryAsync(
            _ => { opens++; return Task.FromException(new IOException()); },
            _ => true,
            (_, token) => { cancellation.Cancel(); return Task.FromCanceled(token); },
            TimeSpan.FromSeconds(5), cancellation.Token));
        Assert.Equal(1, opens);
    }

    [Fact]
    public async Task ExhaustedConnectionBudgetFailsExplicitly() =>
        await Assert.ThrowsAsync<TimeoutException>(() => SqlConnectionRetry.OpenWithRetryAsync(
            token => Task.Delay(Timeout.InfiniteTimeSpan, token),
            _ => false, Task.Delay, TimeSpan.FromMilliseconds(50), default));
}
