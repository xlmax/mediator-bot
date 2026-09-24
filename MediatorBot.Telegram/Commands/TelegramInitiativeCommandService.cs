using MediatorBot.Core;

namespace MediatorBot.Telegram;

public sealed class TelegramInitiativeCommandService(
    TelegramParticipantRegistry participantRegistry,
    IInitiativeStore initiativeStore,
    InitiativeOptions options)
{
    public async Task<TelegramCommandResponse> EnableAsync(
        long telegramUserId,
        CancellationToken cancellationToken = default) =>
        await SetEnabledAsync(telegramUserId, true, cancellationToken);

    public async Task<TelegramCommandResponse> DisableAsync(
        long telegramUserId,
        CancellationToken cancellationToken = default) =>
        await SetEnabledAsync(telegramUserId, false, cancellationToken);

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

        var preferences = await initiativeStore.GetParticipantPreferencesAsync(
            binding.Session,
            cancellationToken);
        var preference = preferences.Single(
            item => item.ParticipantId == binding.Participant.Id);
        var participantStatus = preference.IsEnabled
            ? preference.PauseUntil is DateTimeOffset pauseUntil &&
              pauseUntil > DateTimeOffset.UtcNow
                ? $"временно приостановлены до {pauseUntil:yyyy-MM-dd HH:mm} UTC"
                : "разрешены"
            : "отключены";
        var runtimeStatus = !options.Enabled
            ? "Проактивный heartbeat сейчас отключён системой."
            : options.ShadowMode
                ? "Система работает в ShadowMode и ничего не отправляет."
                : "Система работает в live-режиме.";
        return new TelegramCommandResponse(
            true,
            $"Инициативные сообщения для вас: {participantStatus}. {runtimeStatus}");
    }

    private async Task<TelegramCommandResponse> SetEnabledAsync(
        long telegramUserId,
        bool enabled,
        CancellationToken cancellationToken)
    {
        var binding = await participantRegistry.FindByTelegramUserIdAsync(
            telegramUserId,
            cancellationToken);
        if (binding is null)
        {
            return UnknownUser();
        }

        await initiativeStore.SetParticipantEnabledAsync(
            binding.Session.Id,
            binding.Participant.Id,
            enabled,
            DateTimeOffset.UtcNow,
            cancellationToken);
        return new TelegramCommandResponse(
            true,
            enabled
                ? "Инициативные сообщения снова разрешены. Медиатор всё равно будет писать только при достаточной пользе."
                : "Инициативные сообщения отключены. Обычные ответы на ваши сообщения продолжат работать.");
    }

    private static TelegramCommandResponse UnknownUser() => new(
        false,
        "Этот Telegram-аккаунт не привязан к mediator session.");
}
