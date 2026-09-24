namespace MediatorBot.Infrastructure;

public static class OpenAiToolCatalog
{
    public const string SendToParticipant = "send_to_participant";
    public const string SendToBoth = "send_to_both";
    public const string OpenMediatedRequest = "open_mediated_request";
    public const string ResolveMediatedRequest = "resolve_mediated_request";
    public const string CancelMediatedRequest = "cancel_mediated_request";
    public const string NoAction = "no_action";

    public static IReadOnlyList<OpenAiToolDefinition> All { get; } =
    [
        new(
            SendToParticipant,
            "Send one participant a warm, concrete mediator response that provides useful movement rather than commentary alone.",
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
                  "description": "Natural mediator message grounded in the situation. For an unresolved significant topic, include a useful observation, option, formulation, or next step; avoid unsolicited privacy disclaimers."
                },
                "disclosureDecision": {
                  "type": "string",
                  "enum": ["PrivateResponse", "MediatorDisclosure", "ExplicitTransfer", "SafetyDisclosure"],
                  "description": "Mediation classification. Use PrivateResponse for PrivateSupport to the current author. Use MediatorDisclosure for a SafeParaphrase or initiative BridgeIntervention. Use ExplicitTransfer only when the author unmistakably asked the mediator to send the message; asking for help wording what the author might say is PrivateResponse. SafetyDisclosure is reserved for a safety exception."
                }
              },
              "required": ["participantId", "text", "disclosureDecision"],
              "additionalProperties": false
            }
            """),
        new(
            SendToBoth,
            "Send coordinated private mediator messages to both participants to create one shared bridge or invite them into one bounded mediation step.",
            """
            {
              "type": "object",
              "properties": {
                "textForA": {
                  "type": "string",
                  "description": "Warm and concrete message for Participant A, aligned with the same mediation goal as textForB."
                },
                "textForB": {
                  "type": "string",
                  "description": "Warm and concrete message for Participant B, aligned with the same mediation goal as textForA."
                },
                "disclosureDecision": {
                  "type": "string",
                  "enum": ["MediatorDisclosure", "ExplicitTransfer", "SafetyDisclosure"],
                  "description": "Mediation classification for contacting both participants. Use MediatorDisclosure for a BridgeIntervention or SafeParaphrase, ExplicitTransfer for a requested constructive transfer, and SafetyDisclosure only for a safety exception."
                }
              },
              "required": ["textForA", "textForB", "disclosureDecision"],
              "additionalProperties": false
            }
            """),
        new(
            OpenMediatedRequest,
            "Open a durable, consent-based question from the current author to the other participant.",
            """
            {
              "type": "object",
              "properties": {
                "respondentId": {
                  "type": "string",
                  "description": "ParticipantId of the other participant who may answer or decline."
                },
                "summary": {
                  "type": "string",
                  "description": "Short internal summary used to recognize a later response."
                },
                "textForRequester": {
                  "type": "string",
                  "description": "Acknowledgement explaining that an answer depends on the respondent's consent."
                },
                "textForRespondent": {
                  "type": "string",
                  "description": "Safely paraphrased question with an explicit right not to answer and clear sharing expectations."
                },
                "disclosureDecision": {
                  "type": "string",
                  "enum": ["ExplicitTransfer"]
                }
              },
              "required": ["respondentId", "summary", "textForRequester", "textForRespondent", "disclosureDecision"],
              "additionalProperties": false
            }
            """),
        new(
            ResolveMediatedRequest,
            "Resolve an open mediated request after its respondent answers, declines, or gives no shareable answer.",
            """
            {
              "type": "object",
              "properties": {
                "requestId": {
                  "type": "string",
                  "description": "RequestId from OPEN MEDIATED REQUESTS."
                },
                "outcome": {
                  "type": "string",
                  "enum": ["Answered", "Declined", "NoShareableAnswer"]
                },
                "textForRequester": {
                  "type": "string",
                  "description": "Answer or neutral closure sent to the original requester."
                },
                "textForRespondent": {
                  "type": "string",
                  "description": "Optional private acknowledgement sent to the respondent."
                },
                "disclosureDecision": {
                  "type": "string",
                  "enum": ["ExplicitTransfer", "MediatorDisclosure", "SafetyDisclosure"],
                  "description": "Must be ExplicitTransfer for Answered, MediatorDisclosure for Declined or NoShareableAnswer, or SafetyDisclosure only for an Answered safety exception."
                }
              },
              "required": ["requestId", "outcome", "textForRequester", "disclosureDecision"],
              "additionalProperties": false
            }
            """),
        new(
            CancelMediatedRequest,
            "Cancel an open mediated request when its original requester asks to cancel it.",
            """
            {
              "type": "object",
              "properties": {
                "requestId": {
                  "type": "string",
                  "description": "RequestId from OPEN MEDIATED REQUESTS."
                },
                "textForRequester": {
                  "type": "string",
                  "description": "Cancellation acknowledgement for the requester."
                },
                "textForRespondent": {
                  "type": "string",
                  "description": "Neutral cancellation notice for the respondent."
                },
                "disclosureDecision": {
                  "type": "string",
                  "enum": ["ExplicitTransfer"]
                }
              },
              "required": ["requestId", "textForRequester", "textForRespondent", "disclosureDecision"],
              "additionalProperties": false
            }
            """),
        new(
            NoAction,
            "Send nothing only when silence is more useful than both a concrete response to the current author and a safe mediation step.",
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
