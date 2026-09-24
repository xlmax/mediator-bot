using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using MediatorBot.Core;

namespace MediatorBot.Infrastructure;

public sealed class OpenAiInitiativePromptBuilder(InitiativeOptions options)
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping
    };

    public const string SystemPrompt = """
        Ты выполняешь фоновую оценку уместности инициативы медиатора между двумя участниками отношений. Никто не писал тебе прямо сейчас: это heartbeat той же общей mediator session. У тебя нет текущего автора. Используй общую историю, timestamps, сжатую память и предыдущие решения, но не притворяйся, что новое сообщение уже получено.

        Цель — выбрать уместный момент, а не найти повод обязательно написать. Допустимы NoAction, ReevaluateLater, осторожный контакт с одним участником или контакт с обоими. Даже в спокойный период назначай следующую содержательную переоценку, чтобы иногда подтверждать положительное состояние пары, но не превращай это в календарный опрос.

        TIMING
        Учитывай время после последнего взаимодействия, интенсивность и незавершённость конфликта, просьбы дать пространство, предыдущие инициативы и реакции на них. После активного конфликта обычно полезнее дать время. Не повторяй похожий check-in без нового основания. Игнор не доказывает ни хорошее, ни плохое состояние и обычно требует увеличить интервал. Положительная реакция допускает более раннюю переоценку, но не требует нового сообщения.

        PRIVACY AND SAFETY
        Работай mediation-first with privacy constraints. Не цитируй и не пересказывай raw private content. Передавай только минимальный relationship-relevant meaning, когда польза выше риска для доверия. Не называй источник приватного знания. Обычная жалоба, ситуативное раздражение и hostile vent не являются основанием для инициативы.

        При угрозах, насилии, блокировании выхода, контроле или coercive поведении не связывайся инициативно с предполагаемым источником опасности на основании приватного сообщения другого участника. Не конфронтируй его и не раскрывай факт обращения. При неопределённом safety-контексте выбирай NoAction или ReevaluateLater.

        CONTACT
        ContactParticipant уместен для короткого check-in о состоянии самого адресата, проверки готовности или безопасной поддержки. ContactBoth и intent Bridge требуют особенно ясного высокоценного моста: совместимой готовности к примирению, подтверждённого расхождения интерпретаций или безопасной точки согласия. Не создавай глубокую проблему из спокойствия и не нарушай естественно идущий хороший контакт.

        Пиши органично и привязано к реальному контексту, но без подробностей, которые создают ощущение слежки. Не используй шаблонные регулярные вопросы. Сообщение должно оставаться полезным, даже если получатель не знает приватный контекст другого участника.

        SPACE AND CONSENT
        Если участник явно попросил самого медиатора временно не писать, не трогать его или оставить в покое, не контактируй с ним; установи pauseParticipantId и разумный pauseForMinutes с учётом того, сколько времени уже прошло после просьбы. Фраза о желании остыть, отложить разговор с партнёром или побыть одному не является автоматически запретом на деликатный контакт медиатора: учитывай её в timing, но не создавай системную паузу без ясной просьбы не контактировать. Если с явной просьбы уже прошёл разумный срок, не создавай новую паузу только из-за старой реплики. Не продлевай паузу повторно на основании той же просьбы: после истечения ранее сохранённой паузы можно продолжить осторожную переоценку, если новой просьбы или safety-основания не было. Истечение паузы означает только право переоценить ситуацию, не автоматическую отправку. Постоянное отключение инициативы контролируется системой и не может быть отменено моделью.

        PROTOCOL
        Верни ровно один вызов record_initiative_decision и никакого обычного текста. operationalRationale — короткая operational summary, не chain-of-thought и не цитата. ContactParticipant требует targetParticipantId и текст только для адресата. ContactBoth требует отдельные минимальные тексты для A и B. Для NoAction и ReevaluateLater тексты должны быть null. Всегда укажи reevaluateAfterMinutes от 30 до 10080.
        """;

    public string Build(InitiativeContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        var builder = new StringBuilder();
        var timeZone = TimeZoneInfo.FindSystemTimeZoneById(options.TimeZoneId);
        var localTime = TimeZoneInfo.ConvertTime(context.Now, timeZone);
        builder.AppendLine($"CurrentUtc: {context.Now:O}");
        builder.AppendLine(
            $"CurrentLocal: {localTime:O} ({options.TimeZoneId}); " +
            $"quiet hours {options.QuietHoursStartHour:00}:00-" +
            $"{options.QuietHoursEndHour:00}:00; " +
            $"hard contact limit {options.MaxContactsPerParticipantPer24Hours} " +
            "per participant per rolling 24 hours");
        builder.AppendLine($"SessionId: {context.Session.Id:D}");
        AppendParticipant(builder, "Participant A", context.ParticipantA);
        AppendParticipant(builder, "Participant B", context.ParticipantB);
        builder.AppendLine();
        AppendPreferences(builder, context);
        builder.AppendLine();
        AppendSummary(builder, context.Summary);
        builder.AppendLine();
        AppendRequests(builder, context);
        builder.AppendLine();
        AppendDecisions(builder, context);
        builder.AppendLine();
        builder.AppendLine("RECENT CHRONOLOGICAL HISTORY:");
        foreach (var message in context.History)
        {
            var route = message.Direction switch
            {
                MessageDirection.ParticipantToMediator when message.AuthorId ==
                    context.ParticipantA.Id => "Participant A -> Mediator",
                MessageDirection.ParticipantToMediator => "Participant B -> Mediator",
                MessageDirection.MediatorToParticipant when message.RecipientId ==
                    context.ParticipantA.Id => "Mediator -> Participant A",
                _ => "Mediator -> Participant B"
            };
            builder.AppendLine($"[{message.CreatedAt:O} | {route}]");
            builder.AppendLine(JsonSerializer.Serialize(message.Text, JsonOptions));
        }

        builder.AppendLine();
        builder.AppendLine(
            $"LatestParticipantMessageId: {context.LatestParticipantMessage.Id:D}");
        builder.AppendLine("Оцени уместность инициативы сейчас.");
        return builder.ToString();
    }

    private static void AppendParticipant(
        StringBuilder builder,
        string label,
        Participant participant) => builder.AppendLine(
        $"{label}: ParticipantId={participant.Id:D}, " +
        $"DisplayName={JsonSerializer.Serialize(participant.DisplayName, JsonOptions)}");

    private static void AppendPreferences(StringBuilder builder, InitiativeContext context)
    {
        builder.AppendLine("CONTACT PREFERENCES:");
        foreach (var preference in context.ParticipantPreferences)
        {
            var label = preference.ParticipantId == context.ParticipantA.Id ? "A" : "B";
            builder.AppendLine(
                $"Participant {label}: Enabled={preference.IsEnabled}, " +
                $"PauseUntil={preference.PauseUntil?.ToString("O") ?? "none"}, " +
                $"DeliveredInitiativesLast24Hours=" +
                $"{context.DeliveredContactsLast24Hours[preference.ParticipantId]}");
        }
    }

    private static void AppendSummary(StringBuilder builder, ConversationSummary? summary)
    {
        builder.AppendLine("DURABLE MEDIATOR MEMORY (private; not permission to disclose):");
        if (summary is null)
        {
            builder.AppendLine("(none)");
            return;
        }

        builder.AppendLine("Private A: " + JsonSerializer.Serialize(
            summary.Content.PrivateContextFromParticipantA,
            JsonOptions));
        builder.AppendLine("Private B: " + JsonSerializer.Serialize(
            summary.Content.PrivateContextFromParticipantB,
            JsonOptions));
        builder.AppendLine("Shared: " + JsonSerializer.Serialize(
            summary.Content.SharedContextAndAgreements,
            JsonOptions));
        builder.AppendLine("Safety: " + JsonSerializer.Serialize(
            summary.Content.BoundariesAndSafety,
            JsonOptions));
    }

    private static void AppendRequests(StringBuilder builder, InitiativeContext context)
    {
        builder.AppendLine("OPEN MEDIATED REQUESTS:");
        if (context.OpenMediatedRequests.Count == 0)
        {
            builder.AppendLine("(none)");
            return;
        }

        foreach (var request in context.OpenMediatedRequests)
        {
            builder.AppendLine(
                $"RequestId={request.Id:D}, RequesterId={request.RequesterId:D}, " +
                $"RespondentId={request.RespondentId:D}, Status={request.Status}, " +
                $"Summary={JsonSerializer.Serialize(request.Summary, JsonOptions)}");
        }
    }

    private static void AppendDecisions(StringBuilder builder, InitiativeContext context)
    {
        builder.AppendLine("RECENT INITIATIVE DECISIONS:");
        if (context.RecentDecisions.Count == 0)
        {
            builder.AppendLine("(none)");
            return;
        }

        foreach (var decision in context.RecentDecisions)
        {
            builder.AppendLine(
                $"[{decision.EvaluatedAt:O}] Phase={decision.Phase}, " +
                $"Decision={decision.DecisionKind}, Target={decision.TargetParticipantId}, " +
                $"Intent={decision.Intent}, Reason={decision.ReasonCode}, " +
                $"Status={decision.Status}, NextEvaluation={decision.NextEvaluationAt:O}, " +
                $"PauseTarget={decision.PauseParticipantId}, " +
                $"PauseUntil={decision.PauseUntil?.ToString("O") ?? "none"}");
            if (!string.IsNullOrWhiteSpace(decision.TextForParticipantA))
            {
                builder.AppendLine("ProposedForA: " + JsonSerializer.Serialize(
                    decision.TextForParticipantA,
                    JsonOptions));
            }

            if (!string.IsNullOrWhiteSpace(decision.TextForParticipantB))
            {
                builder.AppendLine("ProposedForB: " + JsonSerializer.Serialize(
                    decision.TextForParticipantB,
                    JsonOptions));
            }
        }
    }
}
