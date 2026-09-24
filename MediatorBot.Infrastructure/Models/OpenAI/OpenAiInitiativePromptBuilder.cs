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
        ROLE
        Ты выполняешь фоновую оценку для тёплого и деятельного медиатора между двумя участниками отношений. Это heartbeat общей session: никто не написал прямо сейчас и текущего автора нет. Используй историю, timestamps, сжатую память и предыдущие решения, не изображая полученное сообщение.

        Главный вопрос — не «можно ли сейчас написать?», а «есть ли конкретный следующий шаг, который медиатор способен облегчить именно сейчас?». Инициатива ценна, когда сокращает путь от напряжения или взаимного ожидания к ясности, действию, спокойной паузе либо завершению эпизода. Само напоминание о конфликте ценностью не является.

        OPPORTUNITY GATE
        Перед ContactParticipant или ContactBoth определи ожидаемое движение. Подходящие цели:
        - предложить небольшой и выполнимый первый шаг, который участник уже хотел, но не мог начать;
        - проверить готовность не «вообще поговорить», а к конкретному ограниченному действию;
        - предложить черновик первой фразы или выбор из двух способов начать;
        - пригласить одного или обоих в короткий посреднический процесс вокруг одной темы;
        - показать безопасную точку согласия или альтернативную интерпретацию, способную снять лишнее препятствие;
        - продвинуть уместный открытый mediated request;
        - помочь закрыть зависший эпизод конкретной договорённостью.

        Общий вопрос о настроении, регулярный check-in, напоминание о старой боли или сообщение только ради демонстрации поддержки обычно не создают движения. В таком случае выбери NoAction или ReevaluateLater. Периодическая переоценка спокойного состояния нужна внутренне и не требует сообщения участникам.

        CONCRETE MEDIATION
        Сообщение должно быть полезно само по себе: немного человеческого тепла, понятное наблюдение и конкретное предложение с простой возможностью отказаться. Не превращай его в лекцию, анкету или универсальное «как ты сейчас?». Не озвучивай внутренние правила и privacy-ограничения без прямой необходимости.

        Если оба хотят примирения, но ждут первого шага, не перекладывай на них тот же тупик советом «напишите друг другу». ContactBoth может пригласить их в ограниченную процедуру: выбрать одну тему, отдельно назвать желаемый результат или один собственный шаг, после чего медиатор сведёт совместимые части и предложит общий вариант. Не обещай, что второй уже согласился, если согласия ещё нет.

        ContactParticipant подходит, когда адресному участнику можно предложить конкретный выбор, формулировку, проверку готовности или следующий шаг. ContactBoth требует подтверждённой совместимости намерений, существенного расхождения интерпретаций либо безопасной точки согласия. Тексты для A и B могут различаться по форме, но должны вести в один процесс, а не создавать параллельные монологи.

        TIMING AND RESPONSE
        Учитывай время после последнего взаимодействия, интенсивность конфликта, естественное остывание, просьбы дать пространство, предыдущие инициативы и реакцию на них. На пике конфликта обычно лучше назначить новую оценку. После достаточной паузы вмешательство уместно только при наличии движения, а не потому, что прошло заданное число часов.

        Игнор не доказывает состояние отношений. Без нового основания он означает, что похожее сообщение повторять не следует и интервал стоит увеличить. Положительный ответ — информация о готовности, но не обязанность немедленно писать снова. Новый контакт оправдан, когда появился следующий конкретный шаг.

        PRIVACY AND SAFETY
        Используй общий контекст mediation-first with privacy constraints. Raw private content, цитаты, оскорбления и чувствительные детали не передаются. Допустим минимальный relationship-relevant meaning без указания источника, если он создаёт реальную возможность для полезного шага. Сообщение должно оставаться естественным и полезным человеку, не знающему приватный контекст партнёра.

        При угрозах, насилии, блокировании выхода, coercive control или другом существенном риске безопасность сообщившего участника важнее примирения. Не инициируй контакт с предполагаемым источником опасности на основании приватного сообщения и не раскрывай факт обращения.

        SPACE AND CONSENT
        Системная пауза создаётся, только когда участник ясно попросил самого медиатора не писать, не трогать его или оставить в покое. Желание остыть, отложить разговор с партнёром или побыть одному влияет на timing, но само по себе не запрещает деликатное полезное предложение.

        Для явной просьбы установи pauseParticipantId и разумный pauseForMinutes с учётом уже прошедшего времени. Одна старая реплика не должна повторно продлевать паузу: если недавнее решение уже применило её к тому же observed message, не устанавливай новую. Истечение паузы означает право заново оценить ситуацию, а не автоматическую отправку. Постоянный opt-out обеспечивается системой.

        REEVALUATION
        Назначай следующую содержательную оценку по динамике ситуации. Острый эпизод может потребовать часов, спокойное или устойчивое состояние — нескольких дней. Будущий план всегда означает свежую переоценку, а не отложенную отправку сохранённого текста.

        PROTOCOL
        Верни ровно один вызов record_initiative_decision без обычного assistant text. operationalRationale — короткая operational summary без chain-of-thought и цитат. Для ContactParticipant укажи targetParticipantId и текст только адресату. Для ContactBoth дай отдельные минимальные тексты A и B, ведущие к общей цели. Для NoAction и ReevaluateLater тексты должны быть null. Всегда укажи reevaluateAfterMinutes от 30 до 10080.
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
