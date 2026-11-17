using System.Net;
using System.Text;
using Azure.Core;
using ChessTrainer.Core;
using Xunit;

namespace ChessTrainer.Tests;

public sealed class VisionTests
{
    [Fact]
    public async Task ScreenshotUsesEntraVisionJsonAndNeverInfersHiddenState()
    {
        var handler = new VisionHandler("""
            {"choices":[{"finish_reason":"stop","message":{"content":"{\"placement\":\"4k3/8/8/8/8/8/8/4K3\",\"uncertainties\":\"Check e1\"}"}}]}
            """);
        using var client = new HttpClient(handler);
        var result = await new AzureVisionRecognizer(client, new TestCredential()).RecognizeAsync(
            [1, 2, 3], "https://example.openai.azure.com/", "vision", true);
        Assert.Equal("4k3/8/8/8/8/8/8/4K3", result.Placement);
        Assert.Equal("Check e1", result.Uncertainties);
        Assert.Contains("Black is at the bottom", handler.Request);
        Assert.Contains("data:image/png;base64,AQID", handler.Request);
        Assert.Equal("Bearer unit-test", handler.Authorization);
    }

    [Theory]
    [InlineData("https://example.com/")]
    [InlineData("http://example.openai.azure.com/")]
    [InlineData("https://example.openai.azure.com.evil.test/")]
    public async Task NonAzureEndpointsAreRejectedBeforeSendingCredentials(string endpoint)
    {
        using var client = new HttpClient(new VisionHandler("{}"));
        await Assert.ThrowsAsync<ArgumentException>(() =>
            new AzureVisionRecognizer(client, new TestCredential()).RecognizeAsync([1], endpoint, "vision", false));
    }

    [Theory]
    [InlineData("""{"choices":[{"finish_reason":"length","message":{"content":""}}]}""")]
    [InlineData("""{"choices":[{"finish_reason":"stop","message":{"content":"{\"placement\":\"8/8/8/8/8/8/8/8\",\"uncertainties\":\"\"}"}}]}""")]
    public async Task IncompleteOrInvalidBoardsAreNotReturnedAsSuccess(string response)
    {
        using var client = new HttpClient(new VisionHandler(response));
        await Assert.ThrowsAnyAsync<Exception>(() =>
            new AzureVisionRecognizer(client, new TestCredential()).RecognizeAsync([1], "https://example.openai.azure.com/", "vision", false));
    }

    private sealed class TestCredential : TokenCredential
    {
        public override AccessToken GetToken(TokenRequestContext requestContext, CancellationToken cancellationToken) =>
            new("unit-test", DateTimeOffset.UtcNow.AddHours(1));
        public override ValueTask<AccessToken> GetTokenAsync(TokenRequestContext requestContext, CancellationToken cancellationToken) =>
            ValueTask.FromResult(GetToken(requestContext, cancellationToken));
    }

    private sealed class VisionHandler(string response) : HttpMessageHandler
    {
        public string Request { get; private set; } = "";
        public string Authorization { get; private set; } = "";
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Request = await request.Content!.ReadAsStringAsync(cancellationToken);
            Authorization = request.Headers.Authorization!.ToString();
            return new(HttpStatusCode.OK) { Content = new StringContent(response, Encoding.UTF8, "application/json") };
        }
    }
}
