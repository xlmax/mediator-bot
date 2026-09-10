namespace MediatorBot.Core;

public interface IMediatorDeliveryRecorder
{
    Task RecordDeliveredAsync(
        Guid sessionId,
        Guid participantId,
        string text,
        CancellationToken cancellationToken = default);
}
