namespace MediatorBot.Infrastructure;

public static class OpenAiToolCatalog
{
    public const string SendToParticipant = "send_to_participant";
    public const string SendToBoth = "send_to_both";
    public const string NoAction = "no_action";

    public static IReadOnlyList<OpenAiToolDefinition> All { get; } =
    [
        new(
            SendToParticipant,
            "Send a private mediator message to exactly one participant in the current session.",
            """
            {
              "type": "object",
              "properties": {
                "participantId": {
                  "type": "string",
                  "description": "ParticipantId of Participant A or Participant B from the current session."
                },
                "text": {
                  "type": "string",
                  "description": "Private message to send to this participant."
                }
              },
              "required": ["participantId", "text"],
              "additionalProperties": false
            }
            """),
        new(
            SendToBoth,
            "Send separate private mediator messages to both participants.",
            """
            {
              "type": "object",
              "properties": {
                "textForA": {
                  "type": "string",
                  "description": "Private message addressed to Participant A."
                },
                "textForB": {
                  "type": "string",
                  "description": "Private message addressed to Participant B."
                }
              },
              "required": ["textForA", "textForB"],
              "additionalProperties": false
            }
            """),
        new(
            NoAction,
            "Do not send any message to either participant at this time.",
            """
            {
              "type": "object",
              "properties": {},
              "required": [],
              "additionalProperties": false
            }
            """)
    ];
}
