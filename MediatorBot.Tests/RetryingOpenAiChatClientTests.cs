using MediatorBot.Infrastructure;

namespace MediatorBot.Tests;

public sealed class RetryingOpenAiChatClientTests
{
    [Fact]
    public async Task CompleteAsync_RetriesTransientProviderFailureUntilSuccess()
    {
        var client = new StubChatClient((attempt, _) =>
            attempt < 3
                ? Task.FromException<OpenAiChatResponse>(
                    new OpenAiProviderException(503, "provider_overloaded"))
                : Task.FromResult(Response()));
        var retryingClient = CreateClient(client);

        var response = await retryingClient.CompleteAsync(Request());

        Assert.Equal("test-model", response.Model);
        Assert.Equal(3, client.CallCount);
    }

    [Fact]
    public async Task CompleteAsync_RetriesRateLimitOnlyOnce()
    {
        var expectedException = new OpenAiProviderException(
            429,
            "rate_limit_exceeded");
        var client = new StubChatClient((_, _) =>
            Task.FromException<OpenAiChatResponse>(expectedException));
        var retryingClient = CreateClient(client);

        var actualException = await Assert.ThrowsAsync<OpenAiProviderException>(() =>
            retryingClient.CompleteAsync(Request()));

        Assert.Same(expectedException, actualException);
        Assert.Equal(2, client.CallCount);
    }

    [Fact]
    public async Task CompleteAsync_DoesNotRetryNonTransientProviderFailure()
    {
        var client = new StubChatClient((_, _) =>
            Task.FromException<OpenAiChatResponse>(
                new OpenAiProviderException(400, "invalid_request")));
        var retryingClient = CreateClient(client);

        await Assert.ThrowsAsync<OpenAiProviderException>(() =>
            retryingClient.CompleteAsync(Request()));

        Assert.Equal(1, client.CallCount);
    }

    [Fact]
    public async Task CompleteAsync_RetriesTransientMalformedResponseOnlyOnce()
    {
        var client = new StubChatClient((attempt, _) =>
            attempt == 1
                ? Task.FromException<OpenAiChatResponse>(
                    new OpenAiProtocolException(
                        OpenAiProtocolFailureReason.NoChoices,
                        "No choices."))
                : Task.FromResult(Response()));
        var retryingClient = CreateClient(client);

        await retryingClient.CompleteAsync(Request());

        Assert.Equal(2, client.CallCount);
    }

    [Fact]
    public async Task CompleteAsync_DoesNotRetrySemanticToolProtocolFailure()
    {
        var client = new StubChatClient((_, _) =>
            Task.FromException<OpenAiChatResponse>(
                new OpenAiProtocolException(
                    OpenAiProtocolFailureReason.MissingToolCall,
                    "No tool call.")));
        var retryingClient = CreateClient(client);

        await Assert.ThrowsAsync<OpenAiProtocolException>(() =>
            retryingClient.CompleteAsync(Request()));

        Assert.Equal(1, client.CallCount);
    }

    [Fact]
    public async Task CompleteAsync_NormalizesExhaustedNetworkFailure()
    {
        var client = new StubChatClient((_, _) =>
            Task.FromException<OpenAiChatResponse>(
                new HttpRequestException("Simulated network failure.")));
        var retryingClient = CreateClient(client);

        var exception = await Assert.ThrowsAsync<OpenAiProviderException>(() =>
            retryingClient.CompleteAsync(Request()));

        Assert.Equal("network_error", exception.ProviderErrorType);
        Assert.Equal(3, client.CallCount);
    }

    [Fact]
    public async Task CompleteAsync_StopsAtOverallRequestTimeout()
    {
        var client = new StubChatClient(async (_, cancellationToken) =>
        {
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            return Response();
        });
        var retryingClient = CreateClient(
            client,
            requestTimeout: TimeSpan.FromMilliseconds(30));

        var exception = await Assert.ThrowsAsync<OpenAiProviderException>(() =>
            retryingClient.CompleteAsync(Request()));

        Assert.Equal(408, exception.ProviderCode);
        Assert.Equal("client_timeout", exception.ProviderErrorType);
        Assert.Equal(1, client.CallCount);
    }

    private static RetryingOpenAiChatClient CreateClient(
        IOpenAiChatClient innerClient,
        TimeSpan? requestTimeout = null) =>
        new(
            innerClient,
            new OpenAiModelRuntimeOptions
            {
                ApiKey = "not-used-by-test-client",
                Model = "test-model",
                MaxOutputTokens = 100,
                MaxAttempts = 3,
                RequestTimeout = requestTimeout ?? TimeSpan.FromSeconds(5),
                RetryBaseDelay = TimeSpan.FromMilliseconds(1),
                RetryMaxDelay = TimeSpan.FromMilliseconds(1)
            });

    private static OpenAiChatRequest Request() => new(
        "system",
        "conversation",
        [],
        100);

    private static OpenAiChatResponse Response() => new(
        [new OpenAiToolCall(OpenAiToolCatalog.NoAction, "{}")],
        null,
        "test-model",
        new OpenAiTokenUsage(1, 0, 1));

    private sealed class StubChatClient(
        Func<int, CancellationToken, Task<OpenAiChatResponse>> handler)
        : IOpenAiChatClient
    {
        private int _callCount;

        public int CallCount => Volatile.Read(ref _callCount);

        public Task<OpenAiChatResponse> CompleteAsync(
            OpenAiChatRequest request,
            CancellationToken cancellationToken = default) =>
            handler(Interlocked.Increment(ref _callCount), cancellationToken);
    }
}
