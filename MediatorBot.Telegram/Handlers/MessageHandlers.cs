using Microsoft.Extensions.Logging;
using TeleFlow.Annotations;
using TeleFlow.Telegram;
using TeleFlow.Telegram.Schema.Abstractions;

namespace MediatorBot.Telegram;

[ChatType(TelegramChatType.Private)]
[UseFilter<AllowedParticipantFilter>]
public sealed class PrivateMessageHandler(
    TelegramMessageProcessor messageProcessor,
    ILogger<PrivateMessageHandler> logger)
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
        var typing = await TryStartTypingAsync(context, cancellationToken);
        try
        {
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
        finally
        {
            await StopTypingAsync(
                typing,
                context.Update.UpdateId,
                telegramUserId);
        }
    }

    private async ValueTask<ChatActionLease?> TryStartTypingAsync(
        MessageContext context,
        CancellationToken cancellationToken)
    {
        try
        {
            return await context.Chat.ActionAsync(
                ChatAction.Typing,
                cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            logger.LogWarning(
                "Failed to start Telegram typing indicator. UpdateId={UpdateId} " +
                "TelegramUserId={TelegramUserId} ErrorType={ErrorType}",
                context.Update.UpdateId,
                context.Sender?.Id,
                exception.GetType().Name);
            return null;
        }
    }

    private async ValueTask StopTypingAsync(
        ChatActionLease? typing,
        long updateId,
        long telegramUserId)
    {
        if (typing is null)
        {
            return;
        }

        try
        {
            await typing.DisposeAsync();
        }
        catch (Exception exception)
        {
            logger.LogWarning(
                "Failed to stop Telegram typing indicator cleanly. " +
                "UpdateId={UpdateId} TelegramUserId={TelegramUserId} " +
                "ErrorType={ErrorType}",
                updateId,
                telegramUserId,
                exception.GetType().Name);
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
