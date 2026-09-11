namespace MediatorBot.Core;

public sealed record ParticipantIdentityBinding(
    Guid ParticipantId,
    string ExternalId);

public interface IParticipantIdentityStore
{
    Task EnsureBindingsAsync(
        Guid sessionId,
        string identityProvider,
        IReadOnlyCollection<ParticipantIdentityBinding> expectedBindings,
        CancellationToken cancellationToken = default);
}
