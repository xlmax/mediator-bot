using System.Reflection;
using MediatorBot.Core;
using MediatorBot.Infrastructure;
using MediatorBot.Telegram;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using TeleFlow.Framework.Hosting;
using TeleFlow.Storage.Memory;
using TeleFlow.Telegram;

var builder = Host.CreateApplicationBuilder(args);
builder.Configuration.Sources.Clear();
builder.Configuration
    .SetBasePath(AppContext.BaseDirectory)
    .AddJsonFile("appsettings.json", optional: false)
    .AddJsonFile(
        $"appsettings.{builder.Environment.EnvironmentName}.json",
        optional: true)
    .AddUserSecrets(Assembly.GetExecutingAssembly(), optional: true)
    .AddEnvironmentVariables()
    .AddCommandLine(args);

var databasePath = RequireConfiguration("Storage:DatabasePath");
var databaseKey = RequireConfiguration("Storage:DatabaseKey");
var botToken = RequireConfiguration("Telegram:BotToken");
var sessionIdText = RequireConfiguration("Telegram:SessionId");
if (!Guid.TryParse(sessionIdText, out var sessionId) || sessionId == Guid.Empty)
{
    throw new InvalidOperationException("Telegram:SessionId must be a non-empty GUID.");
}

var participantAUserId = builder.Configuration.GetValue<long>(
    "Telegram:ParticipantAUserId");
var participantADisplayName = builder.Configuration[
    "Telegram:ParticipantADisplayName"] ?? "A";
var participantBUserId = builder.Configuration.GetValue<long>(
    "Telegram:ParticipantBUserId");
var participantBDisplayName = builder.Configuration[
    "Telegram:ParticipantBDisplayName"] ?? "B";
var maxHistoryMessages = builder.Configuration.GetValue<int?>(
    "Storage:MaxHistoryMessages") ?? 200;
ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maxHistoryMessages);
var deliveryTimeoutSeconds = builder.Configuration.GetValue<int?>(
    "Telegram:DeliveryTimeoutSeconds") ?? 30;
ArgumentOutOfRangeException.ThrowIfNegativeOrZero(deliveryTimeoutSeconds);
var deliveryRecordingTimeoutSeconds = builder.Configuration.GetValue<int?>(
    "Telegram:DeliveryRecordingTimeoutSeconds") ?? 10;
ArgumentOutOfRangeException.ThrowIfNegativeOrZero(deliveryRecordingTimeoutSeconds);
var modelRuntimeName = builder.Configuration["ModelRuntime"] ?? "Fake";
var configuredModel = builder.Configuration["OpenAI:Model"] ?? "gpt-4.1-mini";
var turnProcessingOptions = new TelegramTurnProcessingOptions
{
    MaxAttempts = GetPositiveInt("TurnProcessing:MaxAttempts", 3),
    RetryDelay = TimeSpan.FromSeconds(
        GetPositiveInt("TurnProcessing:RetryDelaySeconds", 15))
};
var compactionConfigured = builder.Configuration.GetValue<bool?>(
    "Compaction:Enabled") ?? true;
var compactionOptions = new ConversationCompactionOptions
{
    Enabled = compactionConfigured &&
        modelRuntimeName.Equals("OpenAI", StringComparison.OrdinalIgnoreCase),
    TriggerMessageCount = GetPositiveInt("Compaction:TriggerMessageCount", 200),
    TriggerHistoryCharacters = GetPositiveInt(
        "Compaction:TriggerHistoryCharacters",
        50_000),
    RetainRecentMessageCount = GetPositiveInt(
        "Compaction:RetainRecentMessageCount",
        80),
    RetainRecentCharacters = GetPositiveInt(
        "Compaction:RetainRecentCharacters",
        20_000),
    MaxSummaryCharacters = GetPositiveInt(
        "Compaction:MaxSummaryCharacters",
        6_000),
    MaxOutputTokens = GetPositiveInt("Compaction:MaxOutputTokens", 2_000),
    OperationTimeout = TimeSpan.FromSeconds(
        GetPositiveInt("Compaction:OperationTimeoutSeconds", 180)),
    RetryDelay = TimeSpan.FromSeconds(
        GetPositiveInt("Compaction:RetryDelaySeconds", 60))
};
var initiativeOptions = new InitiativeOptions
{
    Enabled = builder.Configuration.GetValue<bool?>("Initiative:Enabled") ?? false,
    ShadowMode = builder.Configuration.GetValue<bool?>("Initiative:ShadowMode") ?? false,
    HeartbeatInterval = TimeSpan.FromMinutes(
        GetPositiveInt("Initiative:HeartbeatMinutes", 30)),
    MinimumQuietPeriod = TimeSpan.FromMinutes(
        GetPositiveInt("Initiative:MinimumQuietMinutes", 30)),
    MaxHistoryMessages = GetPositiveInt("Initiative:MaxHistoryMessages", 80),
    RecentDecisionCount = GetPositiveInt("Initiative:RecentDecisionCount", 20),
    MinimumReevaluationMinutes = GetPositiveInt(
        "Initiative:MinimumReevaluationMinutes",
        30),
    MaximumReevaluationMinutes = GetPositiveInt(
        "Initiative:MaximumReevaluationMinutes",
        10_080),
    MaxContactsPerParticipantPer24Hours = GetPositiveInt(
        "Initiative:MaxContactsPerParticipantPer24Hours",
        2),
    MaxDeliveryAttempts = GetPositiveInt("Initiative:MaxDeliveryAttempts", 3),
    QuietHoursStartHour = GetHour("Initiative:QuietHoursStartHour", 22),
    QuietHoursEndHour = GetHour("Initiative:QuietHoursEndHour", 9),
    TimeZoneId = builder.Configuration["Initiative:TimeZoneId"] ?? "Europe/Moscow"
};
if (initiativeOptions.MinimumReevaluationMinutes >
    initiativeOptions.MaximumReevaluationMinutes)
{
    throw new InvalidOperationException(
        "Initiative minimum reevaluation interval cannot exceed the maximum.");
}

_ = TimeZoneInfo.FindSystemTimeZoneById(initiativeOptions.TimeZoneId);

builder.Services.AddSingleton(new SqliteConversationStoreOptions(
    Path.GetFullPath(databasePath, Directory.GetCurrentDirectory()),
    databaseKey));
builder.Services.AddSingleton<SqliteConversationStore>();
builder.Services.AddSingleton<IConversationStore>(services =>
    services.GetRequiredService<SqliteConversationStore>());
builder.Services.AddSingleton<IParticipantIdentityStore>(services =>
    services.GetRequiredService<SqliteConversationStore>());
builder.Services.AddSingleton<IExternalUpdateStore>(services =>
    services.GetRequiredService<SqliteConversationStore>());
builder.Services.AddSingleton<IExternalTurnQueueStore>(services =>
    services.GetRequiredService<SqliteConversationStore>());
builder.Services.AddSingleton<ITurnExecutionStore>(services =>
    services.GetRequiredService<SqliteConversationStore>());
builder.Services.AddSingleton<IMediatedRequestStore>(services =>
    services.GetRequiredService<SqliteConversationStore>());
builder.Services.AddSingleton<IConversationCompactionStore>(services =>
    services.GetRequiredService<SqliteConversationStore>());
builder.Services.AddSingleton<IInitiativeStore>(services =>
    services.GetRequiredService<SqliteConversationStore>());
builder.Services.AddSingleton<IInitiativeDeliveryStore>(services =>
    services.GetRequiredService<SqliteConversationStore>());
builder.Services.AddSingleton(compactionOptions);
builder.Services.AddSingleton(initiativeOptions);
builder.Services.AddSingleton(turnProcessingOptions);
builder.Services.AddSingleton(TimeProvider.System);
builder.Services.AddSingleton<IConversationContextBuilder>(services =>
    new ConversationContextBuilder(
        services.GetRequiredService<IConversationStore>(),
        services.GetRequiredService<IMediatedRequestStore>(),
        maxHistoryMessages,
        services.GetRequiredService<IConversationCompactionStore>()));
builder.Services.AddSingleton<MediatorActionSerializer>();
builder.Services.AddSingleton<ISessionTurnCoordinator, SessionTurnCoordinator>();
builder.Services.AddSingleton<MediationService>();

if (modelRuntimeName.Equals("Fake", StringComparison.OrdinalIgnoreCase))
{
    builder.Services.AddSingleton<IModelRuntime, FakeModelRuntime>();
    builder.Services.AddSingleton<IInitiativeRuntime, DisabledInitiativeRuntime>();
    builder.Services.AddSingleton<
        IConversationSummaryGenerator,
        DisabledConversationSummaryGenerator>();
}
else if (modelRuntimeName.Equals("OpenAI", StringComparison.OrdinalIgnoreCase))
{
    var apiKey = RequireConfiguration("OpenAI:ApiKey");
    var endpointValue = builder.Configuration["OpenAI:Endpoint"];
    if (!string.IsNullOrWhiteSpace(endpointValue) &&
        !Uri.TryCreate(endpointValue, UriKind.Absolute, out _))
    {
        throw new InvalidOperationException(
            "OpenAI:Endpoint must be an absolute URI.");
    }

    var endpoint = string.IsNullOrWhiteSpace(endpointValue)
        ? null
        : new Uri(endpointValue, UriKind.Absolute);
    var maxOutputTokens = builder.Configuration.GetValue<int?>(
        "OpenAI:MaxOutputTokens") ?? 1500;
    var maxAttempts = builder.Configuration.GetValue<int?>(
        "OpenAI:MaxAttempts") ?? 3;
    var requestTimeoutSeconds = builder.Configuration.GetValue<int?>(
        "OpenAI:RequestTimeoutSeconds") ?? 120;
    var retryBaseDelayMilliseconds = builder.Configuration.GetValue<int?>(
        "OpenAI:RetryBaseDelayMilliseconds") ?? 1000;
    var retryMaxDelaySeconds = builder.Configuration.GetValue<int?>(
        "OpenAI:RetryMaxDelaySeconds") ?? 30;

    builder.Services.AddSingleton(new OpenAiModelRuntimeOptions
    {
        ApiKey = apiKey,
        Model = configuredModel,
        Endpoint = endpoint,
        MaxOutputTokens = maxOutputTokens,
        MaxAttempts = maxAttempts,
        RequestTimeout = TimeSpan.FromSeconds(requestTimeoutSeconds),
        RetryBaseDelay = TimeSpan.FromMilliseconds(retryBaseDelayMilliseconds),
        RetryMaxDelay = TimeSpan.FromSeconds(retryMaxDelaySeconds)
    });
    builder.Services.AddSingleton<OpenAiSdkChatClient>();
    builder.Services.AddSingleton<IOpenAiChatClient>(services =>
        new RetryingOpenAiChatClient(
            services.GetRequiredService<OpenAiSdkChatClient>(),
            services.GetRequiredService<OpenAiModelRuntimeOptions>(),
            services.GetRequiredService<ILogger<RetryingOpenAiChatClient>>()));
    builder.Services.AddSingleton<OpenAiConversationPromptBuilder>();
    builder.Services.AddSingleton<OpenAiToolCallMapper>();
    builder.Services.AddSingleton<IModelRuntime, OpenAiModelRuntime>();
    builder.Services.AddSingleton<OpenAiInitiativePromptBuilder>();
    builder.Services.AddSingleton<IInitiativeRuntime, OpenAiInitiativeRuntime>();
    builder.Services.AddSingleton<
        IConversationSummaryGenerator,
        OpenAiConversationSummaryGenerator>();
}
else
{
    throw new InvalidOperationException(
        $"Unknown ModelRuntime '{modelRuntimeName}'. Use 'Fake' or 'OpenAI'.");
}

builder.Services.AddSingleton(new TelegramAdapterOptions
{
    SessionId = sessionId,
    ParticipantAUserId = participantAUserId,
    ParticipantADisplayName = participantADisplayName,
    ParticipantBUserId = participantBUserId,
    ParticipantBDisplayName = participantBDisplayName,
    ModelDisplayName = modelRuntimeName.Equals("OpenAI", StringComparison.OrdinalIgnoreCase)
        ? configuredModel
        : "Fake",
    DeliveryTimeout = TimeSpan.FromSeconds(deliveryTimeoutSeconds),
    DeliveryRecordingTimeout = TimeSpan.FromSeconds(deliveryRecordingTimeoutSeconds)
});
builder.Services.AddSingleton<TelegramParticipantRegistry>();
builder.Services.AddSingleton<AllowedParticipantFilter>();
builder.Services.AddSingleton<TelegramCommandService>();
builder.Services.AddSingleton<TelegramInitiativeCommandService>();
builder.Services.AddSingleton<TelegramTextChunker>();
builder.Services.AddSingleton<TelegramMediatorActionDispatcher>();
builder.Services.AddSingleton<TelegramInitiativeDispatcher>();
builder.Services.AddSingleton<IInitiativeContextBuilder, InitiativeContextBuilder>();
builder.Services.AddSingleton<InitiativeEvaluationService>();
builder.Services.AddSingleton<IConversationCompactionService, ConversationCompactionService>();
builder.Services.AddSingleton<ITelegramQueuedTurnProcessor, TelegramQueuedTurnProcessor>();
builder.Services.AddSingleton<TelegramSessionWorkQueue>();
builder.Services.AddSingleton<TelegramMessageProcessor>();
builder.Services.AddSingleton<ITelegramMessageTransport, TeleFlowMessageTransport>();

builder.Services.AddTelegramBot(options => options.Token = botToken);
builder.Services.AddMemoryStateStorage();
builder.Services.AddTelegramHandlersFromAssembly(Assembly.GetExecutingAssembly());
builder.Services.AddLongPolling();

// Registered before TeleFlow so the encrypted database and Session are ready
// before long polling starts receiving updates.
builder.Services.AddHostedService<TelegramSessionInitializer>();
builder.Services.AddHostedService<InitiativeHeartbeatHostedService>();
builder.Services.AddTeleFlowHostedService();

await builder.Build().RunAsync();

string RequireConfiguration(string key)
{
    var value = builder.Configuration[key];
    if (string.IsNullOrWhiteSpace(value) || value.StartsWith("replace-with-"))
    {
        throw new InvalidOperationException($"Configuration value '{key}' is required.");
    }

    return value;
}

int GetPositiveInt(string key, int defaultValue)
{
    var value = builder.Configuration.GetValue<int?>(key) ?? defaultValue;
    ArgumentOutOfRangeException.ThrowIfNegativeOrZero(value, key);
    return value;
}

int GetHour(string key, int defaultValue)
{
    var value = builder.Configuration.GetValue<int?>(key) ?? defaultValue;
    if (value is < 0 or > 23)
    {
        throw new ArgumentOutOfRangeException(key, value, "Hour must be between 0 and 23.");
    }

    return value;
}
