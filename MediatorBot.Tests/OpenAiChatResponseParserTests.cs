using MediatorBot.Infrastructure;

namespace MediatorBot.Tests;

public sealed class OpenAiChatResponseParserTests
{
    private readonly OpenAiChatResponseParser _parser = new();

    [Fact]
    public void Parse_ReadsCompatibleToolCallAndUsage()
    {
        var response = BinaryData.FromString("""
            {
              "model": "provider/model",
              "choices": [
                {
                  "finish_reason": "tool_calls",
                  "message": {
                    "role": "assistant",
                    "content": null,
                    "tool_calls": [
                      {
                        "id": "call_1",
                        "type": "function",
                        "function": {
                          "name": "no_action",
                          "arguments": "{}"
                        }
                      }
                    ]
                  }
                }
              ],
              "usage": {
                "prompt_tokens": 120,
                "completion_tokens": 8,
                "prompt_tokens_details": {
                  "cached_tokens": 40
                }
              }
            }
            """);

        var result = _parser.Parse(response, "requested-model");

        var toolCall = Assert.Single(result.ToolCalls);
        Assert.Equal("no_action", toolCall.Name);
        Assert.Equal("{}", toolCall.ArgumentsJson);
        Assert.Equal("provider/model", result.Model);
        Assert.Equal(new OpenAiTokenUsage(120, 40, 8), result.Usage);
    }

    [Fact]
    public void Parse_RecognizesProviderErrorWrappedInSuccessfulHttpResponse()
    {
        var response = BinaryData.FromString("""
            {
              "error": "{\"error\":{\"message\":\"Expected max_tokens to be accepted\",\"code\":429}}"
            }
            """);

        var exception = Assert.Throws<OpenAiProviderException>(() =>
            _parser.Parse(response, "requested-model"));

        Assert.Equal(429, exception.ProviderCode);
        Assert.Equal("max_tokens", exception.ProviderParameter);
        Assert.DoesNotContain("Expected max_tokens", exception.Message);
    }

    [Fact]
    public void ParseProviderError_ExtractsOnlySafeMetadataFromHttpFailure()
    {
        var response = BinaryData.FromString("""
            {
              "error": {
                "message": "Private provider explanation must not be propagated",
                "type": "invalid_request_error",
                "param": "tools[0].function.parameters",
                "code": "invalid_schema"
              }
            }
            """);

        var sourceException = new InvalidOperationException("HTTP failure");
        var exception = _parser.ParseProviderError(response, 400, sourceException);

        Assert.Equal(400, exception.ProviderCode);
        Assert.Equal("invalid_request_error", exception.ProviderErrorType);
        Assert.Equal("tools[0].function.parameters", exception.ProviderParameter);
        Assert.Same(sourceException, exception.InnerException);
        Assert.DoesNotContain("Private provider explanation", exception.Message);
    }

    [Fact]
    public void ParseProviderError_ReadsCanonicalOpenRouterMetadata()
    {
        var response = BinaryData.FromString("""
            {
              "error": {
                "message": "upstream details",
                "code": 429,
                "metadata": {
                  "error_type": "provider_overloaded",
                  "provider_name": "DeepInfra"
                }
              }
            }
            """);

        var exception = _parser.ParseProviderError(
            response,
            200,
            new InvalidOperationException("HTTP failure"));

        Assert.Equal(429, exception.ProviderCode);
        Assert.Equal("provider_overloaded", exception.ProviderErrorType);
        Assert.Equal("DeepInfra", exception.ProviderName);
        Assert.DoesNotContain("upstream details", exception.Message);
    }

    [Fact]
    public void Parse_ConvertsEmptyChoicesToControlledProtocolFailure()
    {
        var response = BinaryData.FromString("""
            {
              "model": "provider/model",
              "choices": [],
              "usage": {
                "prompt_tokens": 10,
                "completion_tokens": 0
              }
            }
            """);

        var exception = Assert.Throws<OpenAiProtocolException>(() =>
            _parser.Parse(response, "requested-model"));

        Assert.Equal(OpenAiProtocolFailureReason.NoChoices, exception.Reason);
        Assert.Contains("no chat completion choices", exception.Message);
    }
}
