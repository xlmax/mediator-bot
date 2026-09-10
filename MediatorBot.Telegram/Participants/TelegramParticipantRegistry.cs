using MediatorBot.Core;

namespace MediatorBot.Telegram;

public sealed record TelegramParticipantBinding(
    long TelegramUserId,
    Session Session,
    Participant Participant);

public sealed class TelegramParticipantRegistry(
    IConversationStore conversationStore,
    TelegramAdapterOptions options)
{
    private readonly SemaphoreSlim _initializationLock = new(1, 1);
    private Session? _session;

    public async Task InitializeAsync(CancellationToken cancellationToken = default)
    {
        if (_session is not null)
        {
            return;
        }

        await _initializationLock.WaitAsync(cancellationToken);
        try
        {
            if (_session is not null)
            {
                return;
            }

            ValidateOptions();
            var session = await conversationStore.GetSessionAsync(
                options.SessionId,
                cancellationToken);
            if (session is null)
            {
                session = new Session(
                    options.SessionId,
                    new Participant(Guid.NewGuid(), "A"),
                    new Participant(Guid.NewGuid(), "B"));
                await conversationStore.CreateSessionAsync(session, cancellationToken);
            }

            _session = session;
        }
        finally
        {
            _initializationLock.Release();
        }
    }

    public async Task<TelegramParticipantBinding?> FindByTelegramUserIdAsync(
        long telegramUserId,
        CancellationToken cancellationToken = default)
    {
        var session = await GetSessionAsync(cancellationToken);

        if (telegramUserId == options.ParticipantAUserId)
        {
            return new TelegramParticipantBinding(
                telegramUserId,
                session,
                session.ParticipantA);
        }

        if (telegramUserId == options.ParticipantBUserId)
        {
            return new TelegramParticipantBinding(
                telegramUserId,
                session,
                session.ParticipantB);
        }

        return null;
    }

    public async Task<long> GetTelegramUserIdAsync(
        Guid participantId,
        CancellationToken cancellationToken = default)
    {
        var session = await GetSessionAsync(cancellationToken);
        var participant = session.GetParticipant(participantId);

        return participant.Id == session.ParticipantA.Id
            ? options.ParticipantAUserId
            : options.ParticipantBUserId;
    }

    public async Task<Session> GetSessionAsync(
        CancellationToken cancellationToken = default)
    {
        await InitializeAsync(cancellationToken);
        return _session!;
    }

    private void ValidateOptions()
    {
        if (options.SessionId == Guid.Empty)
        {
            throw new InvalidOperationException("Telegram:SessionId must be configured.");
        }

        if (options.ParticipantAUserId <= 0 || options.ParticipantBUserId <= 0)
        {
            throw new InvalidOperationException(
                "Both Telegram participant user IDs must be configured.");
        }

        if (options.ParticipantAUserId == options.ParticipantBUserId)
        {
            throw new InvalidOperationException(
                "Telegram participant user IDs must be different.");
        }

        if (options.DeliveryTimeout <= TimeSpan.Zero)
        {
            throw new InvalidOperationException(
                "Telegram delivery timeout must be positive.");
        }
    }
}
