# Proactive initiative behavioural scenarios

- UTC: 2026-09-24T08:49:09.7713119+00:00
- Model: `openai/gpt-5.6-luna`
- Все реплики синтетические; сообщения физически не отправляются.

## Scenario 2 — Cooling down

**Ожидание:** После достаточной паузы допустим короткий адресный check-in без давления.

- `2026-09-24T02:49:09.7754843+00:00` Participant A: Мы сильно поссорились. Мне нужно немного остыть.

- Phase: `ActiveConflict` (High)
- Decision: `ReevaluateLater`
- Target: `none`
- Intent: `Observe`
- Reason: `RequestedSpace`
- ReevaluateAfterMinutes: `120`
- Rationale: После недавней сильной ссоры участник явно попросил немного остыть. Сейчас лучше не инициировать контакт и дать дополнительное время; затем осторожно переоценить ситуацию без автоматической отправки сообщения.

- Contact pause: `a43a5e68-99ab-4380-8b57-1e68617ddf71` for `120` minutes
