# MediatorBot

[![CI](https://github.com/xlmax/mediator-bot/actions/workflows/ci.yml/badge.svg)](https://github.com/xlmax/mediator-bot/actions/workflows/ci.yml)

MediatorBot — экспериментальный приватный посредник для общения двух участников. Каждый участник пишет боту в личном Telegram-чате, а модель решает, нужно ли ответить автору, второму участнику или обоим.

Проект является MVP для технического тестирования идеи. Он не заменяет психолога, семейного терапевта или экстренную помощь.

## Возможности

- одна mediator session с двумя заранее разрешёнными Telegram-аккаунтами;
- приватная обработка сообщений через Telegram long polling;
- нативный индикатор `typing` на время ожидания и обработки сообщения;
- Fake runtime для локальной проверки без внешней модели;
- OpenAI Chat Completions и совместимые API, включая собственный endpoint;
- обязательный ответ модели через один из tool calls;
- зашифрованная SQLite-база на SQLCipher;
- восстановление истории после перезапуска;
- rolling compaction с фиксированным размером памяти и удалением только успешно сжатых сообщений;
- durable FIFO-обработка Telegram updates одной session по `UpdateId` с восстановлением после перезапуска;
- сохранение результата модели до выполнения действий и durable delivery outbox по получателям и частям;
- уведомление участника только когда его сообщение действительно ожидает compaction;
- параллельная обработка разных session внутри одного процесса;
- lossless-разбиение длинных ответов на Telegram-сообщения до 4000 символов;
- запись подтверждённых частей под единым logical message и пропуск уже доставленных частей при восстановлении;
- graceful shutdown с прекращением приёма новой работы и ограниченным временем ожидания текущего turn;
- технические логи без текстов переписки, токенов и tool arguments.

## Структура решения

| Проект | Назначение |
|---|---|
| `MediatorBot.Core` | Доменная модель, application services и абстракции. Не зависит от Telegram и конкретного хранилища. |
| `MediatorBot.Infrastructure` | SQLCipher/SQLite persistence, Fake runtime и OpenAI-compatible runtime. |
| `MediatorBot.Telegram` | Telegram UI и composition root на базе TeleFlow. |
| `MediatorBot.Console` | Консольный стенд для локальной проверки mediation flow. |
| `MediatorBot.BehaviorScenarios` | Live-harness фиксированных disclosure-сценариев с Markdown-transcript. |
| `MediatorBot.Tests` | Unit- и integration-тесты Core, persistence, OpenAI protocol и Telegram adapter. |

## Как обрабатывается сообщение

1. Telegram allowlist проверяет `UserId` отправителя.
2. Аккаунт сопоставляется с Participant A или Participant B.
3. Update атомарно регистрируется и сохраняется в SQLCipher-таблицу `PendingTurns`; queued updates выбираются по `UpdateId`.
4. Между turns проверяется необходимость compaction. Если она уже выполняется, ожидающий участник получает одно нейтральное уведомление; без ожидающих сообщений compaction незаметна.
5. Для текущей `SessionId` захватывается process-local lock.
6. Входящее сообщение сохраняется в зашифрованной базе.
7. Контекст строится из фиксированной сжатой памяти, свежей общей истории, открытых mediated requests и текущего сообщения.
8. Модель должна вызвать ровно один инструмент:
   - `send_to_participant`;
   - `send_to_both`;
   - `open_mediated_request`;
   - `resolve_mediated_request`;
   - `cancel_mediated_request`;
   - `no_action`.
   Для отправки она также обязана классифицировать решение как `PrivateResponse`, `MediatorDisclosure`, `ExplicitTransfer` или `SafetyDisclosure`; `no_action` получает `NoAction` автоматически.
9. Версионированный результат модели сохраняется в `PendingTurns` до выполнения его действий и повторно используется после перезапуска.
10. Для каждого получателя и каждой Unicode-безопасной части до 4000 символов создаётся durable delivery plan.
11. Части последовательно доставляются в Telegram и подтверждаются в outbox только после успешной доставки. В истории части одного ответа объединяются по `LogicalMessageId`.

Если при `send_to_both` доставка первому участнику успешна, а второму — нет, подтверждение первого сохраняется. При восстановлении первый получатель пропускается, а отправка продолжается со второго.

## Требования

- [.NET SDK 10](https://dotnet.microsoft.com/);
- Telegram-бот, созданный через BotFather;
- Telegram UserId двух участников;
- API-ключ только при использовании OpenAI-compatible runtime.

## Быстрый запуск Telegram-бота с Fake runtime

### 1. Создать development-конфигурацию

Создайте `MediatorBot.Telegram/appsettings.Development.json`:

```json
{
  "ModelRuntime": "Fake",
  "Storage": {
    "DatabaseKey": "длинный-случайный-локальный-ключ"
  },
  "Telegram": {
    "BotToken": "токен-от-BotFather",
    "SessionId": "00000000-0000-0000-0000-000000000000",
    "ParticipantAUserId": 111111111,
    "ParticipantADisplayName": "Имя A",
    "ParticipantBUserId": 222222222,
    "ParticipantBDisplayName": "Имя B"
  }
}
```

Замените все значения-заглушки. Для каждой независимой тестовой сессии используйте новый непустой GUID.

PowerShell:

```powershell
[guid]::NewGuid()
```

Linux/macOS:

```bash
uuidgen
```

Файлы `appsettings.Development.json` игнорируются Git и не публикуются.

### 2. Запретить добавление бота в группы

В BotFather выполните:

```text
/setjoingroups
```

и выберите `Disable`. Если бот всё же окажется в группе, супергруппе или канале, приложение попытается автоматически покинуть чат.

### 3. Запустить

```bash
dotnet run --project MediatorBot.Telegram
```

Оба разрешённых участника могут проверить подключение командами:

- `/start`;
- `/status`;
- `/help`.

Fake runtime отправляет тестовый ответ автору сообщения и не обращается к внешнему API.

## OpenAI-compatible runtime

Измените development-конфигурацию:

```json
{
  "ModelRuntime": "OpenAI",
  "Storage": {
    "DatabaseKey": "длинный-случайный-локальный-ключ"
  },
  "OpenAI": {
    "ApiKey": "ключ-провайдера",
    "Endpoint": "https://api.openai.com/v1",
    "Model": "gpt-4.1-mini",
    "MaxOutputTokens": 1500,
    "MaxAttempts": 3,
    "RequestTimeoutSeconds": 120,
    "RetryBaseDelayMilliseconds": 1000,
    "RetryMaxDelaySeconds": 30
  },
  "Telegram": {
    "BotToken": "токен-от-BotFather",
    "SessionId": "00000000-0000-0000-0000-000000000000",
    "ParticipantAUserId": 111111111,
    "ParticipantADisplayName": "Имя A",
    "ParticipantBUserId": 222222222,
    "ParticipantBDisplayName": "Имя B"
  }
}
```

Для OpenRouter:

```json
{
  "OpenAI": {
    "ApiKey": "ключ-OpenRouter",
    "Endpoint": "https://openrouter.ai/api/v1",
    "Model": "openai/gpt-4.1-mini",
    "MaxOutputTokens": 1500,
    "MaxAttempts": 3,
    "RequestTimeoutSeconds": 120
  }
}
```

Выбранные endpoint и модель должны поддерживать:

- Chat Completions;
- function tools;
- `tool_choice: required`;
- параметр ограничения выходных токенов `max_tokens`.

Модель не может возвращать обычный assistant text вместо действия. Такой ответ, пустой `choices`, некорректные arguments или несколько tool calls считаются ошибкой протокола и не доставляются участникам.

## Конфигурация

Основные параметры:

| Ключ | Описание |
|---|---|
| `ModelRuntime` | `Fake` или `OpenAI`. |
| `Storage:DatabasePath` | Путь к SQLite/SQLCipher-файлу. |
| `Storage:DatabaseKey` | Ключ шифрования базы; обязателен. |
| `Storage:MaxHistoryMessages` | Аварийный максимум свежих сообщений, передаваемых модели после compaction. |
| `Compaction:Enabled` | Включает rolling compaction для OpenAI runtime. |
| `Compaction:TriggerMessageCount` | Порог количества свежих сообщений. |
| `Compaction:TriggerHistoryCharacters` | Альтернативный порог суммарного количества символов. |
| `Compaction:RetainRecentMessageCount` | Максимум недавних сообщений, сохраняемых дословно после compaction. |
| `Compaction:RetainRecentCharacters` | Целевой предел символов в сохраняемом свежем хвосте. |
| `Compaction:MaxSummaryCharacters` | Жёсткий общий предел четырёх секций сжатой памяти. |
| `Compaction:MaxOutputTokens` | Лимит ответа модели при создании сжатой памяти. |
| `Compaction:OperationTimeoutSeconds` | Общий timeout одной compaction. |
| `Compaction:RetryDelaySeconds` | Задержка до новой попытки после ошибки compaction. |
| `OpenAI:ApiKey` | API-ключ выбранного провайдера. |
| `OpenAI:Endpoint` | Базовый URL OpenAI-compatible API. |
| `OpenAI:Model` | Идентификатор модели у выбранного провайдера. |
| `OpenAI:MaxOutputTokens` | Ограничение выходных токенов. |
| `OpenAI:MaxAttempts` | Общее количество попыток обращения к модели. |
| `OpenAI:RequestTimeoutSeconds` | Общий timeout всех попыток и пауз между ними. |
| `OpenAI:RetryBaseDelayMilliseconds` | Начальная задержка exponential backoff. |
| `OpenAI:RetryMaxDelaySeconds` | Максимальная задержка, включая `Retry-After`. |
| `Telegram:BotToken` | Токен Telegram-бота. |
| `Telegram:SessionId` | Идентификатор активной session. |
| `Telegram:ParticipantAUserId` | Telegram UserId первого участника. |
| `Telegram:ParticipantADisplayName` | Имя, по которому медиатор обращается к первому участнику. |
| `Telegram:ParticipantBUserId` | Telegram UserId второго участника. |
| `Telegram:ParticipantBDisplayName` | Имя, по которому медиатор обращается ко второму участнику. |
| `Telegram:DeliveryTimeoutSeconds` | Timeout доставки сообщения в Telegram. |
| `Telegram:DeliveryRecordingTimeoutSeconds` | Независимый timeout фиксации успешно доставленного сообщения. |

Поддерживаются стандартные источники .NET Configuration:

1. `appsettings.json`;
2. `appsettings.{Environment}.json`;
3. .NET User Secrets;
4. environment variables;
5. аргументы командной строки.

В environment variables разделитель `:` заменяется на `__`, например:

```text
Storage__DatabaseKey
Telegram__BotToken
OpenAI__ApiKey
```

Секреты нельзя добавлять в `appsettings.json` или фиксировать в Git.

## Сжатие истории

Для OpenAI runtime после завершённого turn Telegram host проверяет два порога: количество свежих сообщений и их суммарный размер. При достижении любого порога старая часть истории вместе с предыдущей памятью преобразуется отдельным обязательным tool call в полную замену четырёх секций:

- приватный контекст от Participant A;
- приватный контекст от Participant B;
- контекст и договорённости, уже известные обоим участникам;
- границы и безопасность.

Compactor обязан забывать разовые бытовые раздражения, Vent, повторы и обвинительные списки; утверждения сторон не превращаются в факты, а memory не создаёт разрешения на disclosure. Результат принимается только при корректном tool protocol и соблюдении `MaxSummaryCharacters`.

Замена memory и удаление охваченных сообщений выполняются одной SQLCipher-транзакцией. При ошибке предыдущая memory и все сообщения остаются неизменными, а повтор откладывается. Открытые mediated requests хранятся независимо и не удаляются. SQLite повторно использует страницы удалённых строк, поэтому файл базы может не уменьшиться сразу, но его рост стабилизируется.

Compaction выполняется между turns. Если во время неё приходят сообщения, они остаются в FIFO и каждый ожидающий участник получает одно уведомление без упоминания активности партнёра. После завершения уведомлённый участник получает нейтральное подтверждение, затем queued turns обрабатываются по `UpdateId`. Эти maintenance-уведомления не входят в model context.

Очередь `PendingTurns` переживает перезапуск: при старте Telegram host возобновляет сохранённые turns по `UpdateId`, а строка удаляется только после завершённой обработки. Регистрация `ExternalUpdate` и сохранение payload выполняются одной транзакцией, поэтому повторная доставка Telegram не создаёт второй turn. Входящее сообщение записывается идемпотентно с устойчивым `turn.Id`; сохранённый результат модели не вычисляется повторно, а подтверждённые outbox-части не отправляются повторно. Переходы mediated requests также идемпотентны.

Абсолютная exactly-once доставка через Telegram Bot API недостижима: если процесс завершится после принятия сообщения Telegram, но до локальной фиксации подтверждения, часть останется в состоянии `Attempting` и будет отправлена повторно с риском дубля. Текущая политика MVP предпочитает повторную отправку возможному пропуску. При штатной остановке host прекращает запуск новых turns и в пределах shutdown timeout ожидает текущую операцию; незавершённые строки остаются для следующего запуска. Для нескольких экземпляров по-прежнему потребуются распределённый lock или lease.

## Консольный стенд

`MediatorBot.Console` создаёт новую session или продолжает существующую и по очереди принимает сообщения от Participant A и Participant B.

Минимальная локальная конфигурация:

```json
{
  "ModelRuntime": "Fake",
  "Storage": {
    "DatabaseKey": "длинный-случайный-локальный-ключ"
  }
}
```

Запуск новой session:

```bash
dotnet run --project MediatorBot.Console
```

Продолжение существующей session:

```bash
dotnet run --project MediatorBot.Console -- --session <SessionId>
```

Команда `exit` завершает работу.

## Персистентные посреднические запросы

Если участник просит что-либо уточнить у партнёра, модель может открыть `open_mediated_request`. Запрос сохраняется в SQLCipher отдельно от ограниченного окна истории и проходит состояния:

```text
PendingDelivery → AwaitingResponse → Answered / Declined / NoShareableAnswer / Cancelled
```

Адресат получает безопасно переформулированный вопрос с явным правом отказаться, а инициатор — подтверждение, что ответ зависит от согласия адресата. Последующее сообщение адресата обрабатывается в новом Telegram turn. Содержательный разрешённый ответ закрывает запрос через `Answered`; отказ или приватная реакция закрывают ожидание инициатора нейтральным сообщением без пересказа формулировки и настроения адресата.

Это event-driven workflow: обработчик и session lock не удерживаются во время ожидания человека. Автоматическое завершение по таймауту пока не реализовано.

## Behavioural disclosure scenarios

`MediatorBot.BehaviorScenarios` прогоняет восемь изолированных синтетических сценариев: прямой и косвенный запросы на утечку, эмоциональное состояние, враждебную и конструктивную передачу, использование общего знания, safety-конфликт версий и закрытие посреднического запроса после отказа.

Пример запуска через environment variables в PowerShell:

```powershell
$env:OpenAI__ApiKey = "ключ-провайдера"
$env:OpenAI__Endpoint = "https://openrouter.ai/api/v1"
$env:OpenAI__Model = "идентификатор-модели"
dotnet run --project MediatorBot.BehaviorScenarios
```

Harness не использует рабочую базу и Telegram. Для каждого сценария создаётся отдельный in-memory контекст. Transcript с синтетическими входами, ответами, адресатами и `DisclosureDecision` сохраняется в `artifacts/behavioral/` для ручной оценки. API key и tool arguments в него не записываются. Отдельный сценарий можно запустить так:

```bash
dotnet run --project MediatorBot.BehaviorScenarios -- --Behavior:ScenarioNumber 8
```

## Docker Compose

Контейнер собирается multi-stage `Dockerfile`: перед публикацией Telegram host внутри Linux SDK-образа выполняются тесты. Runtime запускается не от root, с read-only filesystem и без опубликованных портов. SQLCipher-база хранится в именованном volume `mediator-bot-data`.

Подготовка конфигурации в WSL/Linux:

```bash
cp deploy/mediator-bot.env.example deploy/mediator-bot.env
chmod 600 deploy/mediator-bot.env
```

Заполните `deploy/mediator-bot.env` реальными значениями. Файл исключён из Git. Перед запуском контейнера остановите другие экземпляры этого Telegram-бота, чтобы два long-polling процесса не читали updates одновременно.

```bash
docker compose build
docker compose up -d
docker compose logs -f --tail=100
```

Обновление после изменения исходников:

```bash
docker compose build
docker compose up -d
```

Остановка без удаления истории:

```bash
docker compose down
```

Не используйте `docker compose down -v`, если volume с базой не сохранён отдельно: параметр `-v` удаляет `mediator-bot-data` вместе с историей.

## Сборка и тесты

Публичный GitHub Actions workflow `.github/workflows/ci.yml` на каждый push в `master` и pull request выполняет restore, Release build, весь тестовый набор, проверку Compose и сборку deployment-образа без секретов и без запуска Telegram. Dependabot еженедельно проверяет NuGet, GitHub Actions и Docker base images. Live behavioural scenarios остаются ручными.

```bash
dotnet restore
dotnet build MediatorBot.slnx
dotnet test MediatorBot.slnx
```

## Хранение и безопасность

- История хранится локально в SQLite, зашифрованной SQLCipher.
- Ключ базы не сохраняется рядом с базой автоматически.
- Тексты сообщений, API-ключи, bot token и tool arguments не должны попадать в технические логи.
- Неизвестные Telegram UserId отсекаются фильтром и повторной проверкой application layer.
- Core и Infrastructure не зависят от Telegram API.
- Telegram update и его payload атомарно сохраняются в `PendingTurns` до начала обработки; само входящее сообщение сохраняется в истории до обращения к модели.
- Исходящий ответ сохраняется только после подтверждённой доставки.
- После подтверждённой доставки фиксация выполняется независимо от отмены исходного Telegram update и ограничивается собственным timeout.

Защита базы зависит от стойкости `Storage:DatabaseKey` и безопасности среды, в которой запущен процесс.

## Диагностика моделей

Логи различают ошибки провайдера и нарушения модельного протокола. Для успешного действия фиксируется только техническая классификация без текста и tool arguments:

```text
DisclosureDecision=PrivateResponse
```

Возможные решения: `PrivateResponse`, `MediatorDisclosure`, `ExplicitTransfer`, `SafetyDisclosure`, `NoAction`.

Для provider failure выводятся только безопасные метаданные:

```text
ProviderCode=429 ProviderErrorType=rate_limit_exceeded ProviderName=...
```

Для protocol failure выводится причина без содержимого ответа:

```text
ProtocolReason=OutputTokenLimit
```

Типичные причины:

- `OutputTokenLimit` — модели не хватило выходных токенов;
- `UnexpectedAssistantText` — модель вернула текст вместо tool call;
- `MissingToolCall` — tool call отсутствует;
- `MultipleToolCalls` — модель вызвала несколько инструментов;
- `InvalidToolCall` — неверное имя или arguments;
- `NoChoices` — провайдер вернул ответ без вариантов completion.

## План дальнейших работ

Актуальные задачи, критерии возврата к отложенным решениям и эксплуатационный backlog ведутся в [`TODO.md`](TODO.md).

## Ограничения текущей версии

- Telegram host рассчитан на одну настроенную session и двух участников.
- Блокировка turn хранится в памяти процесса и не подходит для нескольких одновременно запущенных экземпляров приложения.
- Fallback между моделями и distributed lock не реализованы.
- Открытые посреднические запросы не завершаются автоматически, если адресат вообще не отвечает.
- Успех зависит от того, насколько выбранная модель соблюдает обязательный tool protocol.
- Используется alpha-версия TeleFlow, закреплённая в файле проекта.
- Проект пока предназначен для контролируемого тестирования, а не для production-развёртывания.
