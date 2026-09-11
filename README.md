# MediatorBot

MediatorBot — экспериментальный приватный посредник для общения двух участников. Каждый участник пишет боту в личном Telegram-чате, а модель решает, нужно ли ответить автору, второму участнику или обоим.

Проект является MVP для технического тестирования идеи. Он не заменяет психолога, семейного терапевта или экстренную помощь.

## Возможности

- одна mediator session с двумя заранее разрешёнными Telegram-аккаунтами;
- приватная обработка сообщений через Telegram long polling;
- Fake runtime для локальной проверки без внешней модели;
- OpenAI Chat Completions и совместимые API, включая собственный endpoint;
- обязательный ответ модели через один из tool calls;
- зашифрованная SQLite-база на SQLCipher;
- восстановление истории после перезапуска;
- последовательная обработка сообщений одной session;
- параллельная обработка разных session внутри одного процесса;
- lossless-разбиение длинных ответов на Telegram-сообщения до 4000 символов;
- запись каждой части исходящего сообщения только после успешной доставки;
- технические логи без текстов переписки, токенов и tool arguments.

## Структура решения

| Проект | Назначение |
|---|---|
| `MediatorBot.Core` | Доменная модель, application services и абстракции. Не зависит от Telegram и конкретного хранилища. |
| `MediatorBot.Infrastructure` | SQLCipher/SQLite persistence, Fake runtime и OpenAI-compatible runtime. |
| `MediatorBot.Telegram` | Telegram UI и composition root на базе TeleFlow. |
| `MediatorBot.Console` | Консольный стенд для локальной проверки mediation flow. |
| `MediatorBot.Tests` | Unit- и integration-тесты Core, persistence, OpenAI protocol и Telegram adapter. |

## Как обрабатывается сообщение

1. Telegram allowlist проверяет `UserId` отправителя.
2. Аккаунт сопоставляется с Participant A или Participant B.
3. Для текущей `SessionId` захватывается process-local lock.
4. Входящее сообщение сохраняется в зашифрованной базе.
5. Из общей хронологии строится контекст модели.
6. Модель должна вызвать ровно один инструмент:
   - `send_to_participant`;
   - `send_to_both`;
   - `no_action`.
7. Длинный ответ без усечения разбивается по Unicode-безопасным границам на части до 4000 символов.
8. Части последовательно доставляются в Telegram и записываются в историю только после успешной доставки.

Если при `send_to_both` доставка первому участнику успешна, а второму — нет, в истории остаётся только успешно доставленный ответ.

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
    "ParticipantBUserId": 222222222
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
    "ParticipantBUserId": 222222222
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
| `Storage:MaxHistoryMessages` | Максимум сообщений, передаваемых модели в контексте. |
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
| `Telegram:ParticipantBUserId` | Telegram UserId второго участника. |
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

## Сборка и тесты

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
- Входящее сообщение сохраняется до обращения к модели, поэтому оно остаётся в истории и при ошибке провайдера.
- Исходящий ответ сохраняется только после подтверждённой доставки.
- После подтверждённой доставки фиксация выполняется независимо от отмены исходного Telegram update и ограничивается собственным timeout.

Защита базы зависит от стойкости `Storage:DatabaseKey` и безопасности среды, в которой запущен процесс.

## Диагностика моделей

Логи различают ошибки провайдера и нарушения модельного протокола.

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

## Ограничения текущей версии

- Telegram host рассчитан на одну настроенную session и двух участников.
- Блокировка turn хранится в памяти процесса и не подходит для нескольких одновременно запущенных экземпляров приложения.
- Fallback между моделями и distributed lock не реализованы.
- Успех зависит от того, насколько выбранная модель соблюдает обязательный tool protocol.
- Используется alpha-версия TeleFlow, закреплённая в файле проекта.
- Проект пока предназначен для контролируемого тестирования, а не для production-развёртывания.
