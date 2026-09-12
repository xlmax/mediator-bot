using MediatorBot.Core;

namespace MediatorBot.Tests;

public sealed class MediatorActionSerializerTests
{
    [Fact]
    public void SerializeAndDeserialize_RoundTripsAllActionTypes()
    {
        var participantA = Guid.NewGuid();
        var participantB = Guid.NewGuid();
        var requestId = Guid.NewGuid();
        MediatorAction[] actions =
        [
            new SendToParticipant(
                participantA,
                "private",
                DisclosureDecision.PrivateResponse),
            new SendToBoth(
                "for-a",
                "for-b",
                DisclosureDecision.SafetyDisclosure),
            new OpenMediatedRequest(
                requestId,
                participantA,
                participantB,
                "summary",
                "requester",
                "respondent",
                DisclosureDecision.ExplicitTransfer),
            new ResolveMediatedRequest(
                requestId,
                participantA,
                participantB,
                MediatedRequestOutcome.NoShareableAnswer,
                "resolved-requester",
                "resolved-respondent",
                DisclosureDecision.MediatorDisclosure),
            new CancelMediatedRequest(
                requestId,
                participantA,
                participantB,
                "cancel-requester",
                "cancel-respondent",
                DisclosureDecision.MediatorDisclosure),
            new NoAction()
        ];
        var serializer = new MediatorActionSerializer();

        var serialized = serializer.Serialize(actions);
        var restored = serializer.Deserialize(serialized);

        Assert.Equal(actions, restored);
    }

    [Fact]
    public void Deserialize_RejectsUnknownSchemaVersion()
    {
        var serializer = new MediatorActionSerializer();

        var exception = Assert.Throws<InvalidDataException>(() =>
            serializer.Deserialize("{\"version\":999,\"actions\":[]}"));

        Assert.Contains("unsupported version", exception.Message);
    }
}
