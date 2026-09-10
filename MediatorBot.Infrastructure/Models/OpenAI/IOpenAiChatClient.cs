namespace MediatorBot.Infrastructure;

public interface IOpenAiChatClient
{
    Task<OpenAiChatResponse> CompleteAsync(
        OpenAiChatRequest request,
        CancellationToken cancellationToken = default);
}
