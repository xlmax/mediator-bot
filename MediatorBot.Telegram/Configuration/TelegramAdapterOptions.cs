namespace MediatorBot.Telegram;

public sealed class TelegramAdapterOptions
{
    public Guid SessionId { get; init; }

    public long ParticipantAUserId { get; init; }

    public string ParticipantADisplayName { get; init; } = "A";

    public long ParticipantBUserId { get; init; }

    public string ParticipantBDisplayName { get; init; } = "B";

    public required string ModelDisplayName { get; init; }

    public TimeSpan DeliveryTimeout { get; init; } = TimeSpan.FromSeconds(30);

    public TimeSpan DeliveryRecordingTimeout { get; init; } = TimeSpan.FromSeconds(10);
}
