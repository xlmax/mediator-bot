using MediatorBot.Core;

namespace MediatorBot.Telegram;

public sealed record TelegramCommandResponse(bool IsAuthorized, string Text);

public sealed class TelegramCommandService(
    TelegramParticipantRegistry participantRegistry,
    IConversationStore conversationStore,
    TelegramAdapterOptions options)
{
    public async Task<TelegramCommandResponse> GetStartAsync(
        long telegramUserId,
        CancellationToken cancellationToken = default)
    {
        var binding = await participantRegistry.FindByTelegramUserIdAsync(
            telegramUserId,
            cancellationToken);
        if (binding is null)
        {
            return UnknownUser();
        }

        var role = binding.Participant.Id == binding.Session.ParticipantA.Id
            ? "Participant A"
            : "Participant B";
        return new TelegramCommandResponse(
            true,
            $"Вы подключены к активной mediator session как {role} " +
            $"({binding.Participant.DisplayName}). Сообщения второго участника остаются приватными.");
    }

    public async Task<TelegramCommandResponse> GetStatusAsync(
        long telegramUserId,
        CancellationToken cancellationToken = default)
    {
        var binding = await participantRegistry.FindByTelegramUserIdAsync(
            telegramUserId,
            cancellationToken);
        if (binding is null)
        {
            return UnknownUser();
        }

        var history = await conversationStore.GetHistoryAsync(
            binding.Session.Id,
            cancellationToken: cancellationToken);
        return new TelegramCommandResponse(
            true,
            $"Session активна. Модель: {options.ModelDisplayName}. " +
            $"Сообщений в общей истории: {history.Count}.");
    }

    public async Task<TelegramCommandResponse> GetHelpAsync(
        long telegramUserId,
        CancellationToken cancellationToken = default)
    {
        var binding = await participantRegistry.FindByTelegramUserIdAsync(
            telegramUserId,
            cancellationToken);
        if (binding is null)
        {
            return UnknownUser();
        }

        return new TelegramCommandResponse(
            true,
            "Это приватный посредник для общения о взаимоотношениях двух участников. " +
            "Напишите сообщение в личном чате, и медиатор решит, кому и как ответить. " +
            "Команды: /start, /status, /help.");
    }

    private static TelegramCommandResponse UnknownUser() => new(
        false,
        "Этот Telegram-аккаунт не привязан к mediator session.");
}
