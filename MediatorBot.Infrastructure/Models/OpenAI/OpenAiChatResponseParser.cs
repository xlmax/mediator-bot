using System.Text.Json;

namespace MediatorBot.Infrastructure;

public sealed class OpenAiChatResponseParser
{
    public OpenAiChatResponse Parse(
        BinaryData responseData,
        string requestedModel,
        TimeSpan? retryAfter = null)
    {
        ArgumentNullException.ThrowIfNull(responseData);
        ArgumentException.ThrowIfNullOrWhiteSpace(requestedModel);

        try
        {
            using var document = JsonDocument.Parse(responseData);
            var root = document.RootElement;
            if (root.TryGetProperty("error", out var providerError))
            {
                throw CreateProviderException(
                    providerError,
                    retryAfter: retryAfter);
            }

            if (!root.TryGetProperty("choices", out var choices) ||
                choices.ValueKind != JsonValueKind.Array ||
                choices.GetArrayLength() == 0)
            {
                throw new OpenAiProtocolException(
                    OpenAiProtocolFailureReason.NoChoices,
                    "The provider returned no chat completion choices.");
            }

            var choice = choices[0];
            var finishReason = GetOptionalString(choice, "finish_reason");
            if (finishReason == "length")
            {
                throw new OpenAiProtocolException(
                    OpenAiProtocolFailureReason.OutputTokenLimit,
                    "The provider stopped because the output token limit was reached.");
            }

            if (finishReason == "content_filter")
            {
                throw new OpenAiProtocolException(
                    OpenAiProtocolFailureReason.ContentFilter,
                    "The provider omitted output because of its content filter.");
            }

            if (!choice.TryGetProperty("message", out var message) ||
                message.ValueKind != JsonValueKind.Object)
            {
                throw new OpenAiProtocolException(
                    OpenAiProtocolFailureReason.MissingAssistantMessage,
                    "The provider returned no assistant message.");
            }

            var toolCalls = ParseToolCalls(message);
            var assistantText = ParseAssistantText(message);
            var model = GetOptionalString(root, "model") ?? requestedModel;
            var usage = ParseUsage(root);

            return new OpenAiChatResponse(
                toolCalls,
                assistantText,
                model,
                usage);
        }
        catch (JsonException exception)
        {
            throw new OpenAiProtocolException(
                OpenAiProtocolFailureReason.InvalidJson,
                "The provider returned invalid chat completion JSON.",
                exception);
        }
    }

    public OpenAiProviderException ParseProviderError(
        BinaryData responseData,
        int httpStatus,
        Exception innerException,
        TimeSpan? retryAfter = null)
    {
        ArgumentNullException.ThrowIfNull(responseData);
        ArgumentNullException.ThrowIfNull(innerException);

        try
        {
            using var document = JsonDocument.Parse(responseData);
            var root = document.RootElement;
            var error = root.TryGetProperty("error", out var providerError)
                ? providerError
                : root;
            return CreateProviderException(
                error,
                httpStatus,
                retryAfter,
                innerException);
        }
        catch (JsonException)
        {
            return new OpenAiProviderException(
                httpStatus,
                retryAfter: retryAfter,
                innerException: innerException);
        }
    }

    private static OpenAiProviderException CreateProviderException(
        JsonElement error,
        int? fallbackCode = null,
        TimeSpan? retryAfter = null,
        Exception? innerException = null) =>
        new(
            TryGetProviderCode(error) ?? fallbackCode,
            TryGetProviderMetadata(error, "type") ??
                TryGetProviderMetadata(error, "error_type"),
            TryGetProviderMetadata(error, "param") ??
                TryFindKnownProviderParameter(error),
            TryGetProviderMetadata(error, "provider_name"),
            retryAfter,
            innerException);

    private static IReadOnlyList<OpenAiToolCall> ParseToolCalls(JsonElement message)
    {
        if (!message.TryGetProperty("tool_calls", out var toolCalls) ||
            toolCalls.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined)
        {
            return [];
        }

        if (toolCalls.ValueKind != JsonValueKind.Array)
        {
            throw new OpenAiProtocolException(
                OpenAiProtocolFailureReason.InvalidToolCall,
                "The provider returned invalid tool calls.");
        }

        var result = new List<OpenAiToolCall>();
        foreach (var toolCall in toolCalls.EnumerateArray())
        {
            if (!toolCall.TryGetProperty("function", out var function) ||
                function.ValueKind != JsonValueKind.Object)
            {
                throw new OpenAiProtocolException(
                    OpenAiProtocolFailureReason.InvalidToolCall,
                    "The provider returned an invalid function tool call.");
            }

            var name = GetOptionalString(function, "name");
            if (string.IsNullOrWhiteSpace(name))
            {
                throw new OpenAiProtocolException(
                    OpenAiProtocolFailureReason.InvalidToolCall,
                    "The provider returned a function tool call without a name.");
            }

            if (!function.TryGetProperty("arguments", out var arguments))
            {
                throw new OpenAiProtocolException(
                    OpenAiProtocolFailureReason.InvalidToolCall,
                    "The provider returned a function tool call without arguments.");
            }

            var argumentsJson = arguments.ValueKind == JsonValueKind.String
                ? arguments.GetString()!
                : arguments.GetRawText();
            result.Add(new OpenAiToolCall(name, argumentsJson));
        }

        return result;
    }

    private static string? ParseAssistantText(JsonElement message)
    {
        if (!message.TryGetProperty("content", out var content) ||
            content.ValueKind == JsonValueKind.Null)
        {
            return null;
        }

        if (content.ValueKind == JsonValueKind.String)
        {
            return content.GetString();
        }

        if (content.ValueKind == JsonValueKind.Array)
        {
            return string.Concat(content
                .EnumerateArray()
                .Where(part =>
                    part.TryGetProperty("text", out var text) &&
                    text.ValueKind == JsonValueKind.String)
                .Select(part => part.GetProperty("text").GetString()));
        }

        throw new OpenAiProtocolException(
            OpenAiProtocolFailureReason.InvalidAssistantContent,
            "The provider returned invalid assistant content.");
    }

    private static OpenAiTokenUsage ParseUsage(JsonElement root)
    {
        if (!root.TryGetProperty("usage", out var usage) ||
            usage.ValueKind != JsonValueKind.Object)
        {
            return new OpenAiTokenUsage(0, 0, 0);
        }

        var cachedInputTokens = 0;
        if (usage.TryGetProperty("prompt_tokens_details", out var details) &&
            details.ValueKind == JsonValueKind.Object)
        {
            cachedInputTokens = GetOptionalInt32(details, "cached_tokens");
        }

        return new OpenAiTokenUsage(
            GetOptionalInt32(usage, "prompt_tokens"),
            cachedInputTokens,
            GetOptionalInt32(usage, "completion_tokens"));
    }

    private static int? TryGetProviderCode(JsonElement error, int depth = 0)
    {
        if (depth > 2)
        {
            return null;
        }

        if (error.ValueKind == JsonValueKind.String)
        {
            try
            {
                using var nested = JsonDocument.Parse(error.GetString()!);
                return TryGetProviderCode(nested.RootElement, depth + 1);
            }
            catch (JsonException)
            {
                return null;
            }
        }

        if (error.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        foreach (var propertyName in new[] { "code", "status_code", "status" })
        {
            if (error.TryGetProperty(propertyName, out var property))
            {
                if (property.ValueKind == JsonValueKind.Number &&
                    property.TryGetInt32(out var numericCode))
                {
                    return numericCode;
                }

                if (property.ValueKind == JsonValueKind.String &&
                    int.TryParse(property.GetString(), out numericCode))
                {
                    return numericCode;
                }
            }
        }

        return error.TryGetProperty("error", out var nestedError)
            ? TryGetProviderCode(nestedError, depth + 1)
            : null;
    }

    private static string? TryFindKnownProviderParameter(
        JsonElement error,
        int depth = 0)
    {
        if (depth > 2)
        {
            return null;
        }

        if (error.ValueKind == JsonValueKind.String)
        {
            var value = error.GetString()!;
            try
            {
                using var nested = JsonDocument.Parse(value);
                return TryFindKnownProviderParameter(
                    nested.RootElement,
                    depth + 1);
            }
            catch (JsonException)
            {
                return FindKnownProviderParameter(value);
            }
        }

        if (error.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        if (error.TryGetProperty("message", out var message) &&
            message.ValueKind == JsonValueKind.String)
        {
            var parameter = FindKnownProviderParameter(message.GetString()!);
            if (parameter is not null)
            {
                return parameter;
            }
        }

        return error.TryGetProperty("error", out var nestedError)
            ? TryFindKnownProviderParameter(nestedError, depth + 1)
            : null;
    }

    private static string? FindKnownProviderParameter(string message)
    {
        string[] knownParameters =
        [
            "parallel_tool_calls",
            "max_completion_tokens",
            "max_tokens",
            "tool_choice",
            "tools",
            "strict",
            "store",
            "messages",
            "model"
        ];

        return knownParameters.FirstOrDefault(parameter =>
            message.Contains(parameter, StringComparison.OrdinalIgnoreCase));
    }

    private static string? TryGetProviderMetadata(
        JsonElement error,
        string propertyName,
        int depth = 0)
    {
        if (depth > 2)
        {
            return null;
        }

        if (error.ValueKind == JsonValueKind.String)
        {
            try
            {
                using var nested = JsonDocument.Parse(error.GetString()!);
                return TryGetProviderMetadata(
                    nested.RootElement,
                    propertyName,
                    depth + 1);
            }
            catch (JsonException)
            {
                return null;
            }
        }

        if (error.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        if (error.TryGetProperty(propertyName, out var property))
        {
            var value = GetSafeProviderMetadataValue(property);
            if (value is not null)
            {
                return value;
            }
        }

        if (error.TryGetProperty("metadata", out var metadata) &&
            metadata.ValueKind == JsonValueKind.Object &&
            metadata.TryGetProperty(propertyName, out var metadataProperty))
        {
            var value = GetSafeProviderMetadataValue(metadataProperty);
            if (value is not null)
            {
                return value;
            }
        }

        return error.TryGetProperty("error", out var nestedError)
            ? TryGetProviderMetadata(nestedError, propertyName, depth + 1)
            : null;
    }

    private static string? GetSafeProviderMetadataValue(JsonElement property)
    {
        if (property.ValueKind != JsonValueKind.String)
        {
            return null;
        }

        var value = property.GetString();
        return !string.IsNullOrWhiteSpace(value) &&
            value.Length <= 120 &&
            value.All(character =>
                char.IsAsciiLetterOrDigit(character) ||
                character is '_' or '-' or '.' or '[' or ']' or '/')
                ? value
                : null;
    }

    private static string? GetOptionalString(JsonElement element, string propertyName) =>
        element.TryGetProperty(propertyName, out var property) &&
        property.ValueKind == JsonValueKind.String
            ? property.GetString()
            : null;

    private static int GetOptionalInt32(JsonElement element, string propertyName) =>
        element.TryGetProperty(propertyName, out var property) &&
        property.TryGetInt32(out var value)
            ? value
            : 0;
}
