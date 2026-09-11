using System.Globalization;
using MediatorBot.Core;

namespace MediatorBot.Telegram;

public sealed record TelegramParticipantBinding(
    long TelegramUserId,
    Session Session,
    Participant Participant);

public sealed class TelegramParticipantRegistry(
    IConversationStore conversationStore,
    IParticipantIdentityStore participantIdentityStore,
    TelegramAdapterOptions options)
{
    private const string IdentityProvider = "telegram";
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
                    new Participant(Guid.NewGuid(), options.ParticipantADisplayName),
                    new Participant(Guid.NewGuid(), options.ParticipantBDisplayName));
                await conversationStore.CreateSessionAsync(session, cancellationToken);
            }

            await participantIdentityStore.EnsureBindingsAsync(
                session.Id,
                IdentityProvider,
                [
                    new ParticipantIdentityBinding(
                        session.ParticipantA.Id,
                        options.ParticipantAUserId.ToString(CultureInfo.InvariantCulture)),
                    new ParticipantIdentityBinding(
                        session.ParticipantB.Id,
                        options.ParticipantBUserId.ToString(CultureInfo.InvariantCulture))
                ],
                cancellationToken);

            if (session.ParticipantA.DisplayName != options.ParticipantADisplayName ||
                session.ParticipantB.DisplayName != options.ParticipantBDisplayName)
            {
                await conversationStore.UpdateParticipantDisplayNamesAsync(
                    session.Id,
                    new Dictionary<Guid, string>
                    {
                        [session.ParticipantA.Id] = options.ParticipantADisplayName,
                        [session.ParticipantB.Id] = options.ParticipantBDisplayName
                    },
                    cancellationToken);
                session = new Session(
                    session.Id,
                    new Participant(
                        session.ParticipantA.Id,
                        options.ParticipantADisplayName),
                    new Participant(
                        session.ParticipantB.Id,
                        options.ParticipantBDisplayName),
                    session.CreatedAt);
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

        ValidateDisplayName(
            options.ParticipantADisplayName,
            "Telegram:ParticipantADisplayName");
        ValidateDisplayName(
            options.ParticipantBDisplayName,
            "Telegram:ParticipantBDisplayName");

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

        if (options.DeliveryRecordingTimeout <= TimeSpan.Zero)
        {
            throw new InvalidOperationException(
                "Telegram delivery recording timeout must be positive.");
        }
    }

    private static void ValidateDisplayName(string displayName, string configurationKey)
    {
        if (string.IsNullOrWhiteSpace(displayName) ||
            displayName.Length > 100 ||
            displayName.Any(char.IsControl))
        {
            throw new InvalidOperationException(
                $"{configurationKey} must contain 1-100 characters without control characters.");
        }
    }
}
