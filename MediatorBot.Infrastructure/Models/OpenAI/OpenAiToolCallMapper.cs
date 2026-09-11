using System.Text.Json;
using MediatorBot.Core;

namespace MediatorBot.Infrastructure;

public sealed class OpenAiToolCallMapper
{
    public const string NeutralRequestClosureText =
        "У меня нет ответа, который я могу тебе передать.";
    public const string NeutralRespondentClosureText =
        "Понял. Я не буду передавать содержание твоего ответа.";

    public IReadOnlyList<MediatorAction> Map(
        ConversationContext context,
        IReadOnlyList<OpenAiToolCall> toolCalls)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(toolCalls);

        return toolCalls.Select(toolCall => Map(context, toolCall)).ToArray();
    }

    public MediatorAction Map(
        ConversationContext context,
        OpenAiToolCall toolCall)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(toolCall);

        try
        {
            using var arguments = JsonDocument.Parse(toolCall.ArgumentsJson);
            return toolCall.Name switch
            {
                OpenAiToolCatalog.SendToParticipant =>
                    MapSendToParticipant(context, arguments.RootElement),
                OpenAiToolCatalog.SendToBoth =>
                    MapSendToBoth(arguments.RootElement),
                OpenAiToolCatalog.OpenMediatedRequest =>
                    MapOpenMediatedRequest(context, arguments.RootElement),
                OpenAiToolCatalog.ResolveMediatedRequest =>
                    MapResolveMediatedRequest(context, arguments.RootElement),
                OpenAiToolCatalog.CancelMediatedRequest =>
                    MapCancelMediatedRequest(context, arguments.RootElement),
                OpenAiToolCatalog.NoAction => new NoAction(),
                _ => throw new InvalidOperationException(
                    $"Unknown OpenAI tool '{toolCall.Name}'.")
            };
        }
        catch (JsonException exception)
        {
            throw new InvalidDataException(
                $"OpenAI tool '{toolCall.Name}' returned invalid JSON arguments.",
                exception);
        }
    }

    private static SendToParticipant MapSendToParticipant(
        ConversationContext context,
        JsonElement arguments)
    {
        var participantId = GetRequiredGuid(arguments, "participantId");
        context.Session.GetParticipant(participantId);
        var disclosureDecision = GetDisclosureDecision(arguments);
        if (disclosureDecision == DisclosureDecision.PrivateResponse &&
            participantId != context.Author.Id)
        {
            throw new InvalidDataException(
                "PrivateResponse can only be addressed to the current author.");
        }

        if (disclosureDecision == DisclosureDecision.ExplicitTransfer &&
            participantId == context.Author.Id)
        {
            throw new InvalidDataException(
                "ExplicitTransfer must be addressed to the other participant.");
        }

        return new SendToParticipant(
            participantId,
            GetRequiredText(arguments, "text"),
            disclosureDecision);
    }

    private static SendToBoth MapSendToBoth(JsonElement arguments)
    {
        var disclosureDecision = GetDisclosureDecision(arguments);
        if (disclosureDecision == DisclosureDecision.PrivateResponse)
        {
            throw new InvalidDataException(
                "PrivateResponse cannot be sent to both participants.");
        }

        return new SendToBoth(
            GetRequiredText(arguments, "textForA"),
            GetRequiredText(arguments, "textForB"),
            disclosureDecision);
    }

    private static OpenMediatedRequest MapOpenMediatedRequest(
        ConversationContext context,
        JsonElement arguments)
    {
        var respondentId = GetRequiredGuid(arguments, "respondentId");
        context.Session.GetParticipant(respondentId);
        if (respondentId == context.Author.Id)
        {
            throw new InvalidDataException(
                "A mediated request must be addressed to the other participant.");
        }

        var disclosureDecision = GetDisclosureDecision(arguments);
        if (disclosureDecision != DisclosureDecision.ExplicitTransfer)
        {
            throw new InvalidDataException(
                "Opening a mediated request requires ExplicitTransfer.");
        }

        return new OpenMediatedRequest(
            Guid.NewGuid(),
            context.Author.Id,
            respondentId,
            GetRequiredText(arguments, "summary"),
            GetRequiredText(arguments, "textForRequester"),
            GetRequiredText(arguments, "textForRespondent"),
            disclosureDecision);
    }

    private static ResolveMediatedRequest MapResolveMediatedRequest(
        ConversationContext context,
        JsonElement arguments)
    {
        var request = GetOpenRequest(context, arguments);
        if (request.RespondentId != context.Author.Id)
        {
            throw new InvalidDataException(
                "Only the respondent can resolve a mediated request.");
        }

        var outcome = GetOutcome(arguments);
        var disclosureDecision = GetDisclosureDecision(arguments);
        var validDecision = outcome switch
        {
            MediatedRequestOutcome.Answered => disclosureDecision is
                DisclosureDecision.ExplicitTransfer or
                DisclosureDecision.SafetyDisclosure,
            MediatedRequestOutcome.Declined or
                MediatedRequestOutcome.NoShareableAnswer =>
                    disclosureDecision == DisclosureDecision.MediatorDisclosure,
            _ => false
        };
        if (!validDecision)
        {
            throw new InvalidDataException(
                "The disclosure decision is inconsistent with the mediated request outcome.");
        }

        var requiresNeutralClosure = outcome is
            MediatedRequestOutcome.Declined or
            MediatedRequestOutcome.NoShareableAnswer;
        var textForRequester = requiresNeutralClosure
            ? NeutralRequestClosureText
            : GetRequiredText(arguments, "textForRequester");
        var textForRespondent = requiresNeutralClosure
            ? NeutralRespondentClosureText
            : GetOptionalText(arguments, "textForRespondent");

        return new ResolveMediatedRequest(
            request.Id,
            request.RequesterId,
            request.RespondentId,
            outcome,
            textForRequester,
            textForRespondent,
            disclosureDecision);
    }

    private static CancelMediatedRequest MapCancelMediatedRequest(
        ConversationContext context,
        JsonElement arguments)
    {
        var request = GetOpenRequest(context, arguments);
        if (request.RequesterId != context.Author.Id)
        {
            throw new InvalidDataException(
                "Only the requester can cancel a mediated request.");
        }

        var disclosureDecision = GetDisclosureDecision(arguments);
        if (disclosureDecision != DisclosureDecision.ExplicitTransfer)
        {
            throw new InvalidDataException(
                "Cancelling a mediated request requires ExplicitTransfer.");
        }

        return new CancelMediatedRequest(
            request.Id,
            request.RequesterId,
            request.RespondentId,
            GetRequiredText(arguments, "textForRequester"),
            GetRequiredText(arguments, "textForRespondent"),
            disclosureDecision);
    }

    private static MediatedRequest GetOpenRequest(
        ConversationContext context,
        JsonElement arguments)
    {
        var requestId = GetRequiredGuid(arguments, "requestId");
        return context.OpenMediatedRequests.SingleOrDefault(request =>
                request.Id == requestId)
            ?? throw new InvalidDataException(
                "OpenAI referenced a mediated request outside the current open requests.");
    }

    private static MediatedRequestOutcome GetOutcome(JsonElement arguments)
    {
        var value = GetRequiredText(arguments, "outcome");
        return value switch
        {
            nameof(MediatedRequestOutcome.Answered) =>
                MediatedRequestOutcome.Answered,
            nameof(MediatedRequestOutcome.Declined) =>
                MediatedRequestOutcome.Declined,
            nameof(MediatedRequestOutcome.NoShareableAnswer) =>
                MediatedRequestOutcome.NoShareableAnswer,
            _ => throw new InvalidDataException(
                "OpenAI returned an invalid mediated request outcome.")
        };
    }

    private static DisclosureDecision GetDisclosureDecision(JsonElement arguments)
    {
        var value = GetRequiredText(arguments, "disclosureDecision");
        return value switch
        {
            nameof(DisclosureDecision.PrivateResponse) =>
                DisclosureDecision.PrivateResponse,
            nameof(DisclosureDecision.MediatorDisclosure) =>
                DisclosureDecision.MediatorDisclosure,
            nameof(DisclosureDecision.ExplicitTransfer) =>
                DisclosureDecision.ExplicitTransfer,
            nameof(DisclosureDecision.SafetyDisclosure) =>
                DisclosureDecision.SafetyDisclosure,
            _ => throw new InvalidDataException(
                "OpenAI returned an invalid disclosure decision.")
        };
    }

    private static Guid GetRequiredGuid(JsonElement arguments, string propertyName)
    {
        var value = GetRequiredText(arguments, propertyName);
        return Guid.TryParse(value, out var result)
            ? result
            : throw new InvalidDataException(
                $"OpenAI tool argument '{propertyName}' must be a valid GUID.");
    }

    private static string GetRequiredText(JsonElement arguments, string propertyName)
    {
        if (!arguments.TryGetProperty(propertyName, out var property) ||
            property.ValueKind != JsonValueKind.String ||
            string.IsNullOrWhiteSpace(property.GetString()))
        {
            throw new InvalidDataException(
                $"OpenAI tool argument '{propertyName}' must be a non-empty string.");
        }

        return property.GetString()!;
    }

    private static string? GetOptionalText(JsonElement arguments, string propertyName)
    {
        if (!arguments.TryGetProperty(propertyName, out var property) ||
            property.ValueKind == JsonValueKind.Null)
        {
            return null;
        }

        if (property.ValueKind != JsonValueKind.String)
        {
            throw new InvalidDataException(
                $"OpenAI tool argument '{propertyName}' must be omitted or a string.");
        }

        var value = property.GetString();
        return string.IsNullOrWhiteSpace(value) ? null : value;
    }
}
