using TeleFlow.Annotations;
using TeleFlow.Telegram;
using TeleFlow.Telegram.Schema.Abstractions;

namespace MediatorBot.Telegram;

[ChatType(TelegramChatType.Private)]
[UseFilter<AllowedParticipantFilter>]
public sealed class PrivateMessageHandler(TelegramMessageProcessor messageProcessor)
{
    [Message]
    [HasText]
    public async Task HandleAsync(
        MessageContext context,
        CancellationToken cancellationToken)
    {
        var telegramUserId = context.Sender?.Id
            ?? throw new InvalidOperationException("Telegram message has no sender.");
        var text = context.TelegramMessage.Text!;
        var status = await messageProcessor.ProcessPrivateTextAsync(
            context.Update.UpdateId,
            telegramUserId,
            text,
            cancellationToken);

        if (status == TelegramMessageProcessingStatus.UnknownUser)
        {
            await context.Message.AnswerAsync(
                "Этот Telegram-аккаунт не привязан к mediator session.",
                cancellationToken);
        }
        else if (status == TelegramMessageProcessingStatus.ModelUnavailable)
        {
            await context.Message.AnswerAsync(
                "Модель временно недоступна. Попробуйте ещё раз позже.",
                cancellationToken);
        }
        else if (status == TelegramMessageProcessingStatus.ModelProtocolFailure)
        {
            await context.Message.AnswerAsync(
                "Не удалось корректно обработать сообщение. Попробуйте ещё раз чуть позже.",
                cancellationToken);
        }
    }
}

[ChatType(TelegramChatType.Group, TelegramChatType.Supergroup)]
public sealed class GroupMessageHandler
{
    [Message]
    [HasText]
    public async Task HandleAsync(
        MessageContext context,
        CancellationToken cancellationToken)
    {
        await context.Bot.LeaveChatAsync(
            IntegerString.From(context.TelegramChat.Id),
            cancellationToken);
    }
}
