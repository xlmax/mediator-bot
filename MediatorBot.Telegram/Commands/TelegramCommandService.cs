using MediatorBot.Core;

namespace MediatorBot.Telegram;

public sealed record TelegramCommandResponse(bool IsAuthorized, string Text);

public sealed class TelegramCommandService(
    TelegramParticipantRegistry participantRegistry,
    IConversationStore conversationStore,
    IConversationCompactionStore compactionStore,
    IExternalTurnQueueStore turnQueueStore,
    TelegramSessionWorkQueue workQueue,
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
            $"({binding.Participant.DisplayName}). Сообщения второго участника остаются приватными. " +
            "В продолжительных диалогах важное сохраняется в краткой памяти медиатора, " +
            "а старые подробные сообщения после успешного сжатия удаляются.");
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

        var historyTask = conversationStore.GetHistoryAsync(
            binding.Session.Id,
            cancellationToken: cancellationToken);
        var summaryTask = compactionStore.GetSummaryAsync(
            binding.Session.Id,
            cancellationToken);
        var failedCountTask = turnQueueStore.GetFailedCountAsync(
            binding.Session.Id,
            binding.Participant.Id,
            cancellationToken);
        await Task.WhenAll(historyTask, summaryTask, failedCountTask);
        var history = await historyTask;
        var summary = await summaryTask;
        var failedCount = await failedCountTask;
        var memoryStatus = summary is null
            ? "краткая память пока не создавалась"
            : $"краткая память обновлена " +
              $"{summary.UpdatedAt.ToUniversalTime():yyyy-MM-dd HH:mm} UTC";
        var failedStatus = failedCount == 0
            ? "необработанных сообщений нет"
            : $"не удалось обработать ваших сообщений: {failedCount}; " +
              "для повторной попытки используйте /retry_failed";
        return new TelegramCommandResponse(
            true,
            $"Session активна. Модель: {options.ModelDisplayName}. " +
            $"Свежих сообщений в общей истории: {history.Count}; {memoryStatus}; " +
            $"{failedStatus}.");
    }

    public async Task<TelegramCommandResponse> RetryFailedAsync(
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

        var retried = await turnQueueStore.RetryFailedAsync(
            binding.Session.Id,
            binding.Participant.Id,
            cancellationToken);
        if (retried == 0)
        {
            return new TelegramCommandResponse(
                true,
                "У вас нет сообщений, ожидающих ручной повторной обработки.");
        }

        await workQueue.RecoverSessionAsync(binding.Session.Id, cancellationToken);
        return new TelegramCommandResponse(
            true,
            "Повторная обработка сохранённых сообщений запущена.");
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
            "При длительном общении бот сохраняет важное в краткой памяти и удаляет " +
            "успешно сжатые старые подробности. " +
            "Команды: /start, /status, /retry_failed, /help.");
    }

    private static TelegramCommandResponse UnknownUser() => new(
        false,
        "Этот Telegram-аккаунт не привязан к mediator session.");
}
