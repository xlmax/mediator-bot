using System.Net;
using System.Text;
using MediatorBot.Core;

namespace MediatorBot.ConsoleApp;

public static class InitiativeHtmlReportGenerator
{
    public static string Generate(
        Session session,
        IReadOnlyList<InitiativeDecision> decisions)
    {
        ArgumentNullException.ThrowIfNull(session);
        ArgumentNullException.ThrowIfNull(decisions);
        var builder = new StringBuilder();
        builder.AppendLine("<!doctype html>");
        builder.AppendLine("<html lang=\"ru\"><head><meta charset=\"utf-8\">");
        builder.AppendLine("<meta name=\"viewport\" content=\"width=device-width\">");
        builder.AppendLine("<title>Initiative timeline</title>");
        builder.AppendLine("""
            <style>
            body{font:15px system-ui;max-width:1000px;margin:30px auto;padding:0 16px;background:#f5f6f8;color:#20242a}
            h1{font-size:24px}.summary,.event{background:white;border:1px solid #d8dce2;border-radius:10px;padding:16px;margin:12px 0}
            .meta{color:#606875}.pill{display:inline-block;padding:3px 8px;margin:2px;border-radius:12px;background:#e8edf5}
            .Delivered{border-left:6px solid #2e8b57}.Failed{border-left:6px solid #b33}.Suppressed,.Superseded{border-left:6px solid #d08b20}
            pre{white-space:pre-wrap;background:#f4f5f7;padding:10px;border-radius:6px}details{margin-top:8px}
            </style></head><body>
            """);
        builder.AppendLine("<h1>Хронология инициатив медиатора</h1>");
        builder.AppendLine("<p class=\"meta\">Отчёт содержит внутренние решения и не предназначен для участников или публикации.</p>");
        var delivered = decisions.Count(item =>
            item.Status == InitiativeDecisionStatus.Delivered);
        var noAction = decisions.Count(item =>
            item.DecisionKind == InitiativeDecisionKind.NoAction);
        builder.AppendLine("<section class=\"summary\">");
        builder.AppendLine($"<b>Решений:</b> {decisions.Count} &nbsp; ");
        builder.AppendLine($"<b>Доставлено:</b> {delivered} &nbsp; ");
        builder.AppendLine($"<b>NoAction:</b> {noAction}");
        builder.AppendLine("</section>");

        foreach (var decision in decisions.OrderByDescending(item => item.EvaluatedAt))
        {
            var target = decision.DecisionKind == InitiativeDecisionKind.ContactBoth
                ? "A и B"
                : decision.TargetParticipantId == session.ParticipantA.Id
                    ? "A"
                    : decision.TargetParticipantId == session.ParticipantB.Id
                        ? "B"
                        : "—";
            builder.AppendLine($"<article class=\"event {decision.Status}\">");
            builder.AppendLine($"<div class=\"meta\">{Encode(decision.EvaluatedAt.ToString("u"))} · {Encode(decision.Id.ToString("D"))}</div>");
            builder.AppendLine($"<span class=\"pill\">{Encode(decision.Phase.ToString())}</span>");
            builder.AppendLine($"<span class=\"pill\">{Encode(decision.DecisionKind.ToString())}</span>");
            builder.AppendLine($"<span class=\"pill\">{Encode(decision.Status.ToString())}</span>");
            builder.AppendLine($"<p><b>Адресат:</b> {target}<br><b>Цель:</b> {Encode(decision.Intent.ToString())}<br><b>Причина:</b> {Encode(decision.ReasonCode.ToString())}<br><b>Следующая оценка:</b> {Encode(decision.NextEvaluationAt.ToString("u"))}</p>");
            if (decision.PauseParticipantId is not null)
            {
                builder.AppendLine($"<p><b>Пауза контакта:</b> {Encode(decision.PauseParticipantId.ToString()!)} до {Encode(decision.PauseUntil?.ToString("u") ?? "—")}</p>");
            }

            builder.AppendLine($"<p>{Encode(decision.OperationalRationale)}</p>");
            if (decision.TextForParticipantA is not null ||
                decision.TextForParticipantB is not null)
            {
                builder.AppendLine("<details><summary>Предложенные сообщения</summary>");
                if (decision.TextForParticipantA is not null)
                {
                    builder.AppendLine($"<b>A</b><pre>{Encode(decision.TextForParticipantA)}</pre>");
                }

                if (decision.TextForParticipantB is not null)
                {
                    builder.AppendLine($"<b>B</b><pre>{Encode(decision.TextForParticipantB)}</pre>");
                }

                builder.AppendLine("</details>");
            }

            builder.AppendLine("</article>");
        }

        builder.AppendLine("</body></html>");
        return builder.ToString();
    }

    private static string Encode(string value) => WebUtility.HtmlEncode(value);
}
