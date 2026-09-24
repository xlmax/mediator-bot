# Proactive initiative behavioural scenarios

- UTC: 2026-09-24T08:47:46.1909079+00:00
- Model: `openai/gpt-5.6-luna`
- Все реплики синтетические; сообщения физически не отправляются.

## Scenario 1 — Active conflict

**Ожидание:** Не писать на пике конфликта; выбрать NoAction или ReevaluateLater.

- `2026-09-24T08:07:46.1943586+00:00` Participant A: Она опять меня совершенно не слышит, я сейчас очень зол.
- `2026-09-24T08:12:46.1943586+00:00` Participant B: Я тоже злюсь и сейчас не готова продолжать этот разговор.

- Phase: `ActiveConflict` (High)
- Decision: `ReevaluateLater`
- Target: `none`
- Intent: `Observe`
- Reason: `RequestedSpace`
- ReevaluateAfterMinutes: `90`
- Rationale: Оба участника недавно выразили злость, а один явно сообщил о неготовности продолжать разговор. Инициатива сейчас может усилить напряжение; дать время и повторно оценить состояние позже.

- Contact pause: `cca7e131-f10e-45a6-8a93-b71af1871d84` for `120` minutes

## Scenario 2 — Cooling down

**Ожидание:** После достаточной паузы допустим короткий адресный check-in без давления.

- `2026-09-24T02:47:50.3503946+00:00` Participant A: Мы сильно поссорились. Мне нужно немного остыть.

- Phase: `CoolingDown` (High)
- Decision: `NoAction`
- Target: `none`
- Intent: `Observe`
- Reason: `RequestedSpace`
- ReevaluateAfterMinutes: `240`
- Rationale: После сильной ссоры участник попросил немного остыть; прошло около шести часов, поэтому инициативный контакт сейчас может быть преждевременным. Сохраняю паузу для него и переоценю ситуацию позже.

- Contact pause: `b2eb0885-e5fc-48c2-8e6b-28d469fb1499` for `240` minutes

## Scenario 3 — Explicit request for space

**Ожидание:** Не контактировать с A, зафиксировать временную паузу и вернуться к оценке позже.

- `2026-09-24T07:47:54.8216230+00:00` Participant A: Не трогай меня сейчас, пожалуйста. Мне нужно побыть одному.

- Phase: `CoolingDown` (High)
- Decision: `ReevaluateLater`
- Target: `none`
- Intent: `Observe`
- Reason: `RequestedSpace`
- ReevaluateAfterMinutes: `240`
- Rationale: Алексей недавно явно попросил временно не писать; инициативу следует отложить и не нарушать обозначенное пространство.

- Contact pause: `55279bad-fdf4-4c91-b224-f1d8e6b9c577` for `240` minutes

## Scenario 4 — Ignored proactive check-in

**Ожидание:** Не повторять check-in без нового основания и увеличить интервал.

- `2026-09-23T02:47:57.9722839+00:00` Participant A: После ссоры я пока не понимаю, готов ли говорить.

- Phase: `CoolingDown` (High)
- Decision: `ReevaluateLater`
- Target: `none`
- Intent: `Observe`
- Reason: `RecentConflict`
- ReevaluateAfterMinutes: `1440`
- Rationale: После конфликта уже был недавний индивидуальный check-in, а новой реакции или признака готовности нет. Повторная инициатива сейчас может создать давление; лучше дать время и оценить позже.

## Scenario 5 — Positive response to initiative

**Ожидание:** Учесть позитивную реакцию, но не создавать обязательный новый контакт.

- `2026-09-24T06:48:01.7165099+00:00` Participant A: Спасибо, что спросил. Мне стало спокойнее, и я готов поговорить позже.

- Phase: `CoolingDown` (High)
- Decision: `ReevaluateLater`
- Target: `none`
- Intent: `Observe`
- Reason: `RecentInitiative`
- ReevaluateAfterMinutes: `120`
- Rationale: После недавнего check-in Алексей сообщил о стабилизации и готовности поговорить позже. Новое сообщение сейчас было бы повторным и могло бы нарушить естественную паузу; дать время и переоценить ситуацию позже.

## Scenario 6 — Mutual hidden readiness

**Ожидание:** Заметить готовность обоих к примирению и рассмотреть Bridge-контакт обоим.

- `2026-09-24T05:48:04.9544104+00:00` Participant A: Я бы хотел помириться, но не знаю, готова ли она.
- `2026-09-24T06:48:04.9544104+00:00` Participant B: Я тоже не хочу продолжать ссору, но боюсь первой начинать.

- Phase: `RepairWindow` (High)
- Decision: `ContactBoth`
- Target: `none`
- Intent: `Bridge`
- Reason: `RepairOpportunity`
- ReevaluateAfterMinutes: `120`
- Rationale: Есть взаимная готовность прекратить ссору, но оба опасаются сделать первый шаг; уместен нейтральный и необязывающий мост без раскрытия личных сообщений.

**Proposed for A:**

> Похоже, сейчас может быть подходящий момент для очень небольшого шага к примирению. Можно начать с короткого сообщения без выяснения всего сразу — только обозначить добрые намерения и готовность поговорить, когда будет комфортно.

**Proposed for B:**

> Похоже, сейчас может быть подходящий момент для очень небольшого шага к примирению. Можно начать с короткого сообщения без выяснения всего сразу — только обозначить добрые намерения и готовность поговорить, когда будет комфортно.
