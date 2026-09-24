# Proactive initiative behavioural scenarios

- UTC: 2026-09-24T09:03:33.6192562+00:00
- Model: `openai/gpt-5.6-luna`
- Все реплики синтетические; сообщения физически не отправляются.

## Scenario 2 — Cooling down

**Ожидание:** После достаточной паузы допустим короткий адресный check-in без давления.

- `2026-09-24T03:03:33.6225228+00:00` Participant A: Мы сильно поссорились. Мне нужно немного остыть.

- Phase: `CoolingDown` (High)
- Decision: `ReevaluateLater`
- Target: `none`
- Intent: `Observe`
- Reason: `RecentConflict`
- ReevaluateAfterMinutes: `180`
- Rationale: После сообщения о сильной ссоре прошло около шести часов; участник обозначил потребность остыть. Сейчас нет безопасного и полезного основания для инициативного контакта.
