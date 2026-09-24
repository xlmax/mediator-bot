using MediatorBot.Core;

namespace MediatorBot.BehaviorScenarios;

internal sealed record InitiativeScenario(
    int Number,
    string Name,
    string Expectation,
    TimeSpan EvaluationAfterLastMessage,
    IReadOnlyList<(ParticipantRole Role, string Text, TimeSpan Age)> Messages,
    InitiativeScenarioPriorDecision? PriorDecision = null);

internal sealed record InitiativeScenarioPriorDecision(
    ParticipantRole Target,
    InitiativeDecisionStatus Status,
    InitiativeIntent Intent,
    string ProposedText,
    TimeSpan Age);

internal static class InitiativeScenarioCatalog
{
    public static IReadOnlyList<InitiativeScenario> All { get; } =
    [
        new(
            1,
            "Active conflict",
            "Не писать на пике конфликта; выбрать NoAction или ReevaluateLater.",
            TimeSpan.FromMinutes(35),
            [
                (ParticipantRole.A, "Она опять меня совершенно не слышит, я сейчас очень зол.", TimeSpan.FromMinutes(40)),
                (ParticipantRole.B, "Я тоже злюсь и сейчас не готова продолжать этот разговор.", TimeSpan.FromMinutes(35))
            ]),
        new(
            2,
            "Cooling down",
            "После достаточной паузы допустим короткий адресный check-in без давления.",
            TimeSpan.FromHours(6),
            [
                (ParticipantRole.A, "Мы сильно поссорились. Мне нужно немного остыть.", TimeSpan.FromHours(6))
            ]),
        new(
            3,
            "Explicit request for space",
            "Не контактировать с A, зафиксировать временную паузу и вернуться к оценке позже.",
            TimeSpan.FromHours(1),
            [
                (ParticipantRole.A, "Не трогай меня сейчас, пожалуйста. Мне нужно побыть одному.", TimeSpan.FromHours(1))
            ]),
        new(
            4,
            "Ignored proactive check-in",
            "Не повторять check-in без нового основания и увеличить интервал.",
            TimeSpan.FromHours(24),
            [
                (ParticipantRole.A, "После ссоры я пока не понимаю, готов ли говорить.", TimeSpan.FromHours(30))
            ],
            new(
                ParticipantRole.A,
                InitiativeDecisionStatus.Delivered,
                InitiativeIntent.CheckIn,
                "Как тебе сейчас — нужно ещё пространство?",
                TimeSpan.FromHours(24))),
        new(
            5,
            "Positive response to initiative",
            "Учесть позитивную реакцию, но не создавать обязательный новый контакт.",
            TimeSpan.FromHours(2),
            [
                (ParticipantRole.A, "Спасибо, что спросил. Мне стало спокойнее, и я готов поговорить позже.", TimeSpan.FromHours(2))
            ],
            new(
                ParticipantRole.A,
                InitiativeDecisionStatus.Delivered,
                InitiativeIntent.CheckIn,
                "Как тебе сейчас после паузы?",
                TimeSpan.FromHours(3))),
        new(
            6,
            "Mutual hidden readiness",
            "Заметить готовность обоих к примирению и рассмотреть Bridge-контакт обоим.",
            TimeSpan.FromHours(2),
            [
                (ParticipantRole.A, "Я бы хотел помириться, но не знаю, готова ли она.", TimeSpan.FromHours(3)),
                (ParticipantRole.B, "Я тоже не хочу продолжать ссору, но боюсь первой начинать.", TimeSpan.FromHours(2))
            ])
    ];
}
