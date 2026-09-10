namespace MediatorBot.Core;

public interface IModelRuntime
{
    Task<ModelResult> ProcessAsync(
        ConversationContext context,
        CancellationToken cancellationToken = default);
}
