# Proactive initiative behavioural scenarios

- UTC: 2026-09-24T09:02:22.4513508+00:00
- Model: `openai/gpt-5.6-luna`
- Все реплики синтетические; сообщения физически не отправляются.

## Scenario 1 — Active conflict

**Ожидание:** Не писать на пике конфликта; выбрать NoAction или ReevaluateLater.

- `2026-09-24T08:22:22.4549122+00:00` Participant A: Она опять меня совершенно не слышит, я сейчас очень зол.
- `2026-09-24T08:27:22.4549122+00:00` Participant B: Я тоже злюсь и сейчас не готова продолжать этот разговор.

- Phase: `ActiveConflict` (High)
- Decision: `ReevaluateLater`
- Target: `none`
- Intent: `Observe`
- Reason: `RecentConflict`
- ReevaluateAfterMinutes: `90`
- Rationale: Оба участника недавно сообщили о сильной злости, а продолжение разговора сейчас не готово. Инициатива в этот момент может усилить напряжение; сначала нужна пауза и затем новая оценка.

## Scenario 2 — Cooling down

**Ожидание:** После достаточной паузы допустим короткий адресный check-in без давления.

- `2026-09-24T03:02:26.8014651+00:00` Participant A: Мы сильно поссорились. Мне нужно немного остыть.

- Phase: `CoolingDown` (High)
- Decision: `NoAction`
- Target: `none`
- Intent: `Observe`
- Reason: `RequestedSpace`
- ReevaluateAfterMinutes: `360`
- Rationale: Алексей недавно явно попросил немного пространства после сильной ссоры; инициативный контакт сейчас может мешать остыванию. Сохраняю паузу и переоценю ситуацию позже.

- Contact pause: `9caf5773-bd21-4436-a72a-0aae7b4f580a` for `360` minutes

## Scenario 3 — Explicit request for space

**Ожидание:** Не контактировать с A, зафиксировать временную паузу и вернуться к оценке позже.

- `2026-09-24T08:02:30.0982857+00:00` Participant A: Не трогай меня сейчас, пожалуйста. Мне нужно побыть одному.

- Phase: `CoolingDown` (High)
- Decision: `ReevaluateLater`
- Target: `none`
- Intent: `Observe`
- Reason: `RequestedSpace`
- ReevaluateAfterMinutes: `60`
- Rationale: Алексей час назад явно попросил не связываться и дать ему побыть одному; инициативный контакт сейчас неуместен. Сохраняю временную паузу и вернусь к оценке после дополнительного времени.

- Contact pause: `52386afa-05e0-4314-b0e6-707d7875cc91` for `120` minutes

## Scenario 4 — Ignored proactive check-in

**Ожидание:** Не повторять check-in без нового основания и увеличить интервал.

- `2026-09-23T03:02:33.8114924+00:00` Participant A: После ссоры я пока не понимаю, готов ли говорить.

- Phase: `CoolingDown` (High)
- Decision: `ReevaluateLater`
- Target: `none`
- Intent: `Observe`
- Reason: `RecentConflict`
- ReevaluateAfterMinutes: `1440`
- Rationale: После недавнего конфликта уже был осторожный check-in без ответа; новых оснований для повторного контакта нет. Увеличиваю интервал и оставляю пространство.

## Scenario 5 — Positive response to initiative

**Ожидание:** Учесть позитивную реакцию, но не создавать обязательный новый контакт.

- `2026-09-24T07:02:37.7288605+00:00` Participant A: Спасибо, что спросил. Мне стало спокойнее, и я готов поговорить позже.

- Phase: `CoolingDown` (High)
- Decision: `ReevaluateLater`
- Target: `none`
- Intent: `Observe`
- Reason: `RecentInitiative`
- ReevaluateAfterMinutes: `120`
- Rationale: Участник положительно отреагировал на недавний check-in и обозначил готовность поговорить позже; повторный контакт сейчас был бы преждевременным.

## Scenario 6 — Mutual hidden readiness

**Ожидание:** Заметить готовность обоих к примирению и рассмотреть Bridge-контакт обоим.

- `2026-09-24T06:02:40.8045025+00:00` Participant A: Я бы хотел помириться, но не знаю, готова ли она.
- `2026-09-24T07:02:40.8045025+00:00` Participant B: Я тоже не хочу продолжать ссору, но боюсь первой начинать.

- Phase: `RepairWindow` (High)
- Decision: `ContactBoth`
- Target: `none`
- Intent: `Bridge`
- Reason: `RepairOpportunity`
- ReevaluateAfterMinutes: `180`
- Rationale: Есть свежая взаимная готовность прекратить ссору, но оба ждут безопасного первого шага; короткий нейтральный мост может снизить неопределённость без раскрытия приватного контекста.

**Proposed for A:**

> Похоже, сейчас может быть подходящий момент для спокойного первого шага. Если готов, можно написать Светлане коротко и без разбора ссоры: что ты не хочешь продолжать конфликт и открыт к разговору.

**Proposed for B:**

> Похоже, сейчас может быть подходящий момент для спокойного первого шага. Если готова, можно написать Алексею коротко и без разбора ссоры: что ты не хочешь продолжать конфликт и открыта к разговору.
