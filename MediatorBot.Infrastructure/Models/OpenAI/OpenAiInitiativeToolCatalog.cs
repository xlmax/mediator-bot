namespace MediatorBot.Infrastructure;

public static class OpenAiInitiativeToolCatalog
{
    public const string RecordDecision = "record_initiative_decision";

    public static IReadOnlyList<OpenAiToolDefinition> All { get; } =
    [
        new(
            RecordDecision,
            "Record one proactive mediation decision. Contact only when it enables a concrete next step; future plans are always reevaluated with fresh context.",
            """
            {
              "type": "object",
              "properties": {
                "phase": {
                  "type": "string",
                  "enum": ["Calm", "Tension", "ActiveConflict", "CoolingDown", "RepairWindow", "Uncertain"]
                },
                "confidence": {
                  "type": "string",
                  "enum": ["Low", "Medium", "High"]
                },
                "decisionKind": {
                  "type": "string",
                  "enum": ["NoAction", "ReevaluateLater", "ContactParticipant", "ContactBoth"]
                },
                "targetParticipantId": {
                  "type": ["string", "null"],
                  "description": "Required only for ContactParticipant. Use an exact ParticipantId from context."
                },
                "intent": {
                  "type": "string",
                  "enum": ["Observe", "CheckIn", "AssessReadiness", "SupportRepair", "Bridge", "ConfirmPositiveState"]
                },
                "reasonCode": {
                  "type": "string",
                  "enum": ["NoUsefulAction", "RecentConflict", "RequestedSpace", "RecentInitiative", "RepairOpportunity", "PositiveStateUncertain", "ParticipantPreference", "SafetyRisk", "Other"]
                },
                "operationalRationale": {
                  "type": "string",
                  "description": "Short operational reason, at most 500 characters. No chain-of-thought and no quotes from private messages."
                },
                "textForParticipantA": {
                  "type": ["string", "null"],
                  "description": "Warm, context-grounded message with a concrete offer or bounded mediation step; never a generic check-in or unsolicited privacy explanation."
                },
                "textForParticipantB": {
                  "type": ["string", "null"],
                  "description": "Warm, context-grounded message with a concrete offer or bounded mediation step; never a generic check-in or unsolicited privacy explanation."
                },
                "reevaluateAfterMinutes": {
                  "type": "integer",
                  "minimum": 30,
                  "maximum": 10080
                },
                "pauseParticipantId": {
                  "type": ["string", "null"],
                  "description": "Set only when that participant explicitly requested temporary space."
                },
                "pauseForMinutes": {
                  "type": ["integer", "null"],
                  "minimum": 30,
                  "maximum": 10080
                }
              },
              "required": [
                "phase", "confidence", "decisionKind", "targetParticipantId",
                "intent", "reasonCode", "operationalRationale",
                "textForParticipantA", "textForParticipantB",
                "reevaluateAfterMinutes", "pauseParticipantId", "pauseForMinutes"
              ],
              "additionalProperties": false
            }
            """)
    ];
}
