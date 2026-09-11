using System.Diagnostics;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace MediatorBot.Infrastructure;

public sealed class RetryingOpenAiChatClient : IOpenAiChatClient
{
    private readonly IOpenAiChatClient _innerClient;
    private readonly OpenAiModelRuntimeOptions _options;
    private readonly ILogger<RetryingOpenAiChatClient> _logger;

    public RetryingOpenAiChatClient(
        IOpenAiChatClient innerClient,
        OpenAiModelRuntimeOptions options,
        ILogger<RetryingOpenAiChatClient>? logger = null)
    {
        ArgumentNullException.ThrowIfNull(innerClient);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentOutOfRangeException.ThrowIfLessThan(options.MaxAttempts, 1);
        if (options.RequestTimeout <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(options.RequestTimeout));
        }

        if (options.RetryBaseDelay <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(options.RetryBaseDelay));
        }

        if (options.RetryMaxDelay < options.RetryBaseDelay)
        {
            throw new ArgumentOutOfRangeException(nameof(options.RetryMaxDelay));
        }

        _innerClient = innerClient;
        _options = options;
        _logger = logger ?? NullLogger<RetryingOpenAiChatClient>.Instance;
    }

    public async Task<OpenAiChatResponse> CompleteAsync(
        OpenAiChatRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        var startedAt = Stopwatch.GetTimestamp();
        using var timeoutCancellation =
            CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutCancellation.CancelAfter(_options.RequestTimeout);

        for (var attempt = 1; ; attempt++)
        {
            try
            {
                var response = await _innerClient.CompleteAsync(
                    request,
                    timeoutCancellation.Token);
                if (attempt > 1)
                {
                    _logger.LogInformation(
                        "Model request succeeded after retry. Model={Model} Attempts={Attempts} " +
                        "DurationMs={DurationMs:F1}",
                        _options.Model,
                        attempt,
                        Stopwatch.GetElapsedTime(startedAt).TotalMilliseconds);
                }

                return response;
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (OperationCanceledException exception)
                when (timeoutCancellation.IsCancellationRequested)
            {
                throw CreateRequestTimeout(exception);
            }
            catch (Exception exception)
            {
                var decision = GetRetryDecision(exception);
                if (decision is null)
                {
                    throw;
                }

                var maxAttempts = Math.Min(_options.MaxAttempts, decision.MaxAttempts);
                if (attempt >= maxAttempts)
                {
                    _logger.LogWarning(
                        "Model request retries exhausted. Model={Model} Attempts={Attempts} " +
                        "RetryReason={RetryReason} DurationMs={DurationMs:F1}",
                        _options.Model,
                        attempt,
                        decision.Reason,
                        Stopwatch.GetElapsedTime(startedAt).TotalMilliseconds);
                    throw NormalizeTransientException(exception, decision.Reason);
                }

                var delay = GetRetryDelay(attempt, decision.RetryAfter);
                var elapsed = Stopwatch.GetElapsedTime(startedAt);
                if (elapsed + delay >= _options.RequestTimeout)
                {
                    throw CreateRequestTimeout(exception);
                }

                _logger.LogWarning(
                    "Model request retry scheduled. Model={Model} Attempt={Attempt} " +
                    "NextAttempt={NextAttempt} RetryReason={RetryReason} DelayMs={DelayMs:F0}",
                    _options.Model,
                    attempt,
                    attempt + 1,
                    decision.Reason,
                    delay.TotalMilliseconds);

                try
                {
                    await Task.Delay(delay, timeoutCancellation.Token);
                }
                catch (OperationCanceledException)
                    when (cancellationToken.IsCancellationRequested)
                {
                    throw;
                }
                catch (OperationCanceledException delayCancellationException)
                    when (timeoutCancellation.IsCancellationRequested)
                {
                    throw CreateRequestTimeout(delayCancellationException);
                }
            }
        }
    }

    private RetryDecision? GetRetryDecision(Exception exception)
    {
        if (exception is OpenAiProviderException providerException)
        {
            var errorType = providerException.ProviderErrorType?.ToLowerInvariant();
            if (errorType == "rate_limit_exceeded" ||
                providerException.ProviderCode == 429)
            {
                return new RetryDecision(
                    "RateLimitExceeded",
                    2,
                    providerException.RetryAfter);
            }

            if (errorType is
                "provider_overloaded" or
                "provider_unavailable" or
                "timeout" or
                "server" ||
                providerException.ProviderCode is 408 or 500 or 502 or 503 or 504)
            {
                return new RetryDecision(
                    errorType ?? $"Provider{providerException.ProviderCode}",
                    int.MaxValue,
                    providerException.RetryAfter);
            }

            return null;
        }

        if (exception is OpenAiProtocolException protocolException &&
            protocolException.Reason is
                OpenAiProtocolFailureReason.NoChoices or
                OpenAiProtocolFailureReason.InvalidJson or
                OpenAiProtocolFailureReason.InvalidResponseShape or
                OpenAiProtocolFailureReason.MissingAssistantMessage or
                OpenAiProtocolFailureReason.InvalidAssistantContent)
        {
            return new RetryDecision(
                protocolException.Reason.ToString(),
                2,
                null);
        }

        if (exception is HttpRequestException or IOException or TimeoutException ||
            exception is OperationCanceledException)
        {
            return new RetryDecision(
                exception is TimeoutException or OperationCanceledException
                    ? "NetworkTimeout"
                    : "NetworkFailure",
                int.MaxValue,
                null);
        }

        return null;
    }

    private TimeSpan GetRetryDelay(int attempt, TimeSpan? retryAfter)
    {
        var exponentialMilliseconds = Math.Min(
            _options.RetryMaxDelay.TotalMilliseconds,
            _options.RetryBaseDelay.TotalMilliseconds * Math.Pow(2, attempt - 1));
        var jitteredMilliseconds = exponentialMilliseconds *
            (1d + Random.Shared.NextDouble());
        var delay = TimeSpan.FromMilliseconds(Math.Min(
            jitteredMilliseconds,
            _options.RetryMaxDelay.TotalMilliseconds));

        if (retryAfter is TimeSpan providerDelay && providerDelay > delay)
        {
            delay = providerDelay <= _options.RetryMaxDelay
                ? providerDelay
                : _options.RetryMaxDelay;
        }

        return delay;
    }

    private static Exception NormalizeTransientException(
        Exception exception,
        string retryReason) =>
        exception is OpenAiProviderException or OpenAiProtocolException
            ? exception
            : new OpenAiProviderException(
                null,
                retryReason == "NetworkTimeout"
                    ? "network_timeout"
                    : "network_error",
                innerException: exception);

    private static OpenAiProviderException CreateRequestTimeout(
        Exception innerException) =>
        new(
            408,
            "client_timeout",
            innerException: innerException);

    private sealed record RetryDecision(
        string Reason,
        int MaxAttempts,
        TimeSpan? RetryAfter);
}
