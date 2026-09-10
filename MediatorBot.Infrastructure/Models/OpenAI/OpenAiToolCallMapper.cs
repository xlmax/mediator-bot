using System.Text.Json;
using MediatorBot.Core;

namespace MediatorBot.Infrastructure;

public sealed class OpenAiToolCallMapper
{
    public IReadOnlyList<MediatorAction> Map(
        Session session,
        IReadOnlyList<OpenAiToolCall> toolCalls)
    {
        ArgumentNullException.ThrowIfNull(session);
        ArgumentNullException.ThrowIfNull(toolCalls);

        return toolCalls.Select(toolCall => Map(session, toolCall)).ToArray();
    }

    public MediatorAction Map(Session session, OpenAiToolCall toolCall)
    {
        ArgumentNullException.ThrowIfNull(session);
        ArgumentNullException.ThrowIfNull(toolCall);

        try
        {
            using var arguments = JsonDocument.Parse(toolCall.ArgumentsJson);
            return toolCall.Name switch
            {
                OpenAiToolCatalog.SendToParticipant =>
                    MapSendToParticipant(session, arguments.RootElement),
                OpenAiToolCatalog.SendToBoth =>
                    new SendToBoth(
                        GetRequiredText(arguments.RootElement, "textForA"),
                        GetRequiredText(arguments.RootElement, "textForB")),
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
        Session session,
        JsonElement arguments)
    {
        var participantIdText = GetRequiredText(arguments, "participantId");
        if (!Guid.TryParse(participantIdText, out var participantId))
        {
            throw new InvalidDataException(
                "OpenAI returned an invalid ParticipantId.");
        }

        session.GetParticipant(participantId);
        return new SendToParticipant(
            participantId,
            GetRequiredText(arguments, "text"));
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
}
