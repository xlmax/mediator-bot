using System.ClientModel;
using System.ClientModel.Primitives;
using System.Text.Json;
using OpenAI;
using OpenAI.Chat;

namespace MediatorBot.Infrastructure;

public sealed class OpenAiSdkChatClient : IOpenAiChatClient
{
    private readonly ChatClient _client;
    private readonly string _model;
    private readonly OpenAiChatResponseParser _responseParser = new();

    public OpenAiSdkChatClient(OpenAiModelRuntimeOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentException.ThrowIfNullOrWhiteSpace(options.ApiKey);
        ArgumentException.ThrowIfNullOrWhiteSpace(options.Model);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(options.MaxOutputTokens);

        _model = options.Model;
        _client = options.Endpoint is null
            ? new ChatClient(options.Model, options.ApiKey)
            : new ChatClient(
                options.Model,
                new ApiKeyCredential(options.ApiKey),
                new OpenAIClientOptions
                {
                    Endpoint = options.Endpoint
                });
    }

    public async Task<OpenAiChatResponse> CompleteAsync(
        OpenAiChatRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        var payload = new
        {
            model = _model,
            messages = new[]
            {
                new { role = "system", content = request.SystemPrompt },
                new { role = "user", content = request.ConversationPrompt }
            },
            tools = request.Tools.Select(tool => new
            {
                type = "function",
                function = new
                {
                    name = tool.Name,
                    description = tool.Description,
                    parameters = ParseParameters(tool.ParametersJson)
                }
            }),
            tool_choice = "required",
            max_tokens = request.MaxOutputTokens
        };

        var requestData = BinaryData.FromBytes(
            JsonSerializer.SerializeToUtf8Bytes(payload));
        using var content = BinaryContent.Create(requestData);
        ClientResult result;
        try
        {
            result = await _client.CompleteChatAsync(
                content,
                new RequestOptions
                {
                    CancellationToken = cancellationToken
                });
        }
        catch (ClientResultException exception)
        {
            var rawResponse = exception.GetRawResponse();
            if (rawResponse is null)
            {
                throw new OpenAiProviderException(
                    exception.Status,
                    innerException: exception);
            }

            throw _responseParser.ParseProviderError(
                rawResponse.Content,
                exception.Status,
                exception);
        }

        return _responseParser.Parse(
            result.GetRawResponse().Content,
            _model);
    }

    private static JsonElement ParseParameters(string parametersJson)
    {
        using var document = JsonDocument.Parse(parametersJson);
        return document.RootElement.Clone();
    }
}
