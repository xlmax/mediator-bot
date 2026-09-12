using System.Text.Json;
using System.Text.Json.Serialization;

namespace MediatorBot.Core;

public sealed class MediatorActionSerializer
{
    private const int CurrentVersion = 1;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Converters = { new JsonStringEnumConverter() }
    };

    public string Serialize(IReadOnlyList<MediatorAction> actions)
    {
        ArgumentNullException.ThrowIfNull(actions);
        if (actions.Count == 0)
        {
            throw new ArgumentException("At least one mediator action is required.", nameof(actions));
        }

        var persisted = actions.Select(ToPersistedAction).ToArray();
        return JsonSerializer.Serialize(
            new PersistedEnvelope(CurrentVersion, persisted),
            JsonOptions);
    }

    public IReadOnlyList<MediatorAction> Deserialize(string json)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(json);
        PersistedEnvelope envelope;
        try
        {
            envelope = JsonSerializer.Deserialize<PersistedEnvelope>(json, JsonOptions)
                ?? throw new InvalidDataException("Persisted mediator actions are empty.");
        }
        catch (JsonException exception)
        {
            throw new InvalidDataException("Persisted mediator actions are malformed.", exception);
        }

        if (envelope.Version != CurrentVersion || envelope.Actions is null ||
            envelope.Actions.Length == 0)
        {
            throw new InvalidDataException(
                "Persisted mediator actions have an unsupported version or no actions.");
        }

        return envelope.Actions.Select(ToMediatorAction).ToArray();
    }

    private static PersistedAction ToPersistedAction(MediatorAction action) => action switch
    {
        SendToParticipant send => new(
            nameof(SendToParticipant),
            send.DisclosureDecision,
            ParticipantId: send.ParticipantId,
            Text: send.Text),
        SendToBoth send => new(
            nameof(SendToBoth),
            send.DisclosureDecision,
            TextForParticipantA: send.TextForParticipantA,
            TextForParticipantB: send.TextForParticipantB),
        OpenMediatedRequest open => new(
            nameof(OpenMediatedRequest),
            open.DisclosureDecision,
            RequestId: open.RequestId,
            RequesterId: open.RequesterId,
            RespondentId: open.RespondentId,
            Summary: open.Summary,
            TextForRequester: open.TextForRequester,
            TextForRespondent: open.TextForRespondent),
        ResolveMediatedRequest resolve => new(
            nameof(ResolveMediatedRequest),
            resolve.DisclosureDecision,
            RequestId: resolve.RequestId,
            RequesterId: resolve.RequesterId,
            RespondentId: resolve.RespondentId,
            Outcome: resolve.Outcome,
            TextForRequester: resolve.TextForRequester,
            TextForRespondent: resolve.TextForRespondent),
        CancelMediatedRequest cancel => new(
            nameof(CancelMediatedRequest),
            cancel.DisclosureDecision,
            RequestId: cancel.RequestId,
            RequesterId: cancel.RequesterId,
            RespondentId: cancel.RespondentId,
            TextForRequester: cancel.TextForRequester,
            TextForRespondent: cancel.TextForRespondent),
        NoAction => new(nameof(NoAction), DisclosureDecision.NoAction),
        _ => throw new ArgumentOutOfRangeException(
            nameof(action),
            action.GetType().Name,
            "Unsupported mediator action type.")
    };

    private static MediatorAction ToMediatorAction(PersistedAction action) =>
        action.ActionType switch
        {
            nameof(SendToParticipant) => new SendToParticipant(
                Require(action.ParticipantId, nameof(action.ParticipantId)),
                Require(action.Text, nameof(action.Text)),
                action.DisclosureDecision),
            nameof(SendToBoth) => new SendToBoth(
                Require(action.TextForParticipantA, nameof(action.TextForParticipantA)),
                Require(action.TextForParticipantB, nameof(action.TextForParticipantB)),
                action.DisclosureDecision),
            nameof(OpenMediatedRequest) => new OpenMediatedRequest(
                Require(action.RequestId, nameof(action.RequestId)),
                Require(action.RequesterId, nameof(action.RequesterId)),
                Require(action.RespondentId, nameof(action.RespondentId)),
                Require(action.Summary, nameof(action.Summary)),
                Require(action.TextForRequester, nameof(action.TextForRequester)),
                Require(action.TextForRespondent, nameof(action.TextForRespondent)),
                action.DisclosureDecision),
            nameof(ResolveMediatedRequest) => new ResolveMediatedRequest(
                Require(action.RequestId, nameof(action.RequestId)),
                Require(action.RequesterId, nameof(action.RequesterId)),
                Require(action.RespondentId, nameof(action.RespondentId)),
                Require(action.Outcome, nameof(action.Outcome)),
                Require(action.TextForRequester, nameof(action.TextForRequester)),
                action.TextForRespondent,
                action.DisclosureDecision),
            nameof(CancelMediatedRequest) => new CancelMediatedRequest(
                Require(action.RequestId, nameof(action.RequestId)),
                Require(action.RequesterId, nameof(action.RequesterId)),
                Require(action.RespondentId, nameof(action.RespondentId)),
                Require(action.TextForRequester, nameof(action.TextForRequester)),
                Require(action.TextForRespondent, nameof(action.TextForRespondent)),
                action.DisclosureDecision),
            nameof(NoAction) when action.DisclosureDecision == DisclosureDecision.NoAction =>
                new NoAction(),
            _ => throw new InvalidDataException(
                $"Unsupported persisted mediator action '{action.ActionType}'.")
        };

    private static T Require<T>(T? value, string propertyName)
        where T : struct => value ?? throw new InvalidDataException(
        $"Persisted mediator action is missing '{propertyName}'.");

    private static string Require(string? value, string propertyName)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            throw new InvalidDataException(
                $"Persisted mediator action is missing '{propertyName}'.");
        }

        return value;
    }

    private sealed record PersistedEnvelope(
        int Version,
        PersistedAction[] Actions);

    private sealed record PersistedAction(
        string ActionType,
        DisclosureDecision DisclosureDecision,
        Guid? ParticipantId = null,
        string? Text = null,
        string? TextForParticipantA = null,
        string? TextForParticipantB = null,
        Guid? RequestId = null,
        Guid? RequesterId = null,
        Guid? RespondentId = null,
        string? Summary = null,
        MediatedRequestOutcome? Outcome = null,
        string? TextForRequester = null,
        string? TextForRespondent = null);
}
