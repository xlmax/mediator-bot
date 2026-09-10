using MediatorBot.Core;

namespace MediatorBot.Infrastructure;

public sealed class FakeModelRuntime : IModelRuntime
{
    public Task<ModelResult> ProcessAsync(
        ConversationContext context,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(context);
        cancellationToken.ThrowIfCancellationRequested();

        MediatorAction action = new SendToParticipant(
            context.Author.Id,
            $"Получил сообщение от {context.Author.DisplayName}: {context.IncomingMessage.Text}");

        return Task.FromResult(new ModelResult([action]));
    }
}
