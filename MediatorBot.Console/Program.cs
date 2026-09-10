using System.Reflection;
using MediatorBot.Core;
using MediatorBot.Infrastructure;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

var builder = Host.CreateApplicationBuilder(args);
builder.Configuration.Sources.Clear();
builder.Configuration
    .SetBasePath(AppContext.BaseDirectory)
    .AddJsonFile("appsettings.json", optional: false)
    .AddUserSecrets(Assembly.GetExecutingAssembly(), optional: true)
    .AddEnvironmentVariables()
    .AddCommandLine(args);

var databasePath = builder.Configuration["Storage:DatabasePath"]
    ?? throw new InvalidOperationException("Storage:DatabasePath is not configured.");
var databaseKey = builder.Configuration["Storage:DatabaseKey"];
if (string.IsNullOrWhiteSpace(databaseKey))
{
    throw new InvalidOperationException(
        "The database key is not configured. Set Storage__DatabaseKey or use " +
        "'dotnet user-secrets set \"Storage:DatabaseKey\" \"<secret>\" " +
        "--project MediatorBot.Console'.");
}

var maxHistoryMessages = builder.Configuration.GetValue<int?>(
    "Storage:MaxHistoryMessages") ?? 100;
ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maxHistoryMessages);
var modelRuntimeName = builder.Configuration["ModelRuntime"] ?? "Fake";

builder.Services.AddSingleton(new SqliteConversationStoreOptions(
    Path.GetFullPath(databasePath, Directory.GetCurrentDirectory()),
    databaseKey));
builder.Services.AddSingleton<SqliteConversationStore>();
builder.Services.AddSingleton<IConversationStore>(services =>
    services.GetRequiredService<SqliteConversationStore>());
builder.Services.AddSingleton<IConversationContextBuilder>(services =>
    new ConversationContextBuilder(
        services.GetRequiredService<IConversationStore>(),
        maxHistoryMessages));

if (modelRuntimeName.Equals("Fake", StringComparison.OrdinalIgnoreCase))
{
    builder.Services.AddSingleton<IModelRuntime, FakeModelRuntime>();
}
else if (modelRuntimeName.Equals("OpenAI", StringComparison.OrdinalIgnoreCase))
{
    var apiKey = builder.Configuration["OpenAI:ApiKey"]
        ?? builder.Configuration["OPENAI_API_KEY"];
    if (string.IsNullOrWhiteSpace(apiKey))
    {
        throw new InvalidOperationException(
            "The OpenAI API key is not configured. Set OpenAI__ApiKey or use " +
            "'dotnet user-secrets set \"OpenAI:ApiKey\" \"<secret>\" " +
            "--project MediatorBot.Console'.");
    }

    var model = builder.Configuration["OpenAI:Model"]
        ?? throw new InvalidOperationException("OpenAI:Model is not configured.");
    var maxOutputTokens = builder.Configuration.GetValue<int?>(
        "OpenAI:MaxOutputTokens") ?? 1500;

    builder.Services.AddSingleton(new OpenAiModelRuntimeOptions
    {
        ApiKey = apiKey,
        Model = model,
        MaxOutputTokens = maxOutputTokens
    });
    builder.Services.AddSingleton<IOpenAiChatClient, OpenAiSdkChatClient>();
    builder.Services.AddSingleton<OpenAiConversationPromptBuilder>();
    builder.Services.AddSingleton<OpenAiToolCallMapper>();
    builder.Services.AddSingleton<IModelRuntime, OpenAiModelRuntime>();
}
else
{
    throw new InvalidOperationException(
        $"Unknown ModelRuntime '{modelRuntimeName}'. Use 'Fake' or 'OpenAI'.");
}

builder.Services.AddSingleton<MediationService>();

using var host = builder.Build();
var store = host.Services.GetRequiredService<SqliteConversationStore>();
await store.InitializeAsync();

var session = await GetOrCreateSessionAsync(
    store,
    builder.Configuration["session"]);
var history = await store.GetHistoryAsync(session.Id);

System.Console.WriteLine($"SessionId: {session.Id}");
System.Console.WriteLine($"ModelRuntime: {modelRuntimeName}");
System.Console.WriteLine($"Восстановлено сообщений: {history.Count}");
System.Console.WriteLine(
    "Для продолжения этой сессии: dotnet run --project MediatorBot.Console -- " +
    $"--session {session.Id}");
System.Console.WriteLine("Вводи сообщения по очереди; 'exit' завершает работу.");

var mediationService = host.Services.GetRequiredService<MediationService>();
var currentParticipant = GetNextParticipant(session, history);
while (true)
{
    System.Console.Write($"{currentParticipant.DisplayName}> ");
    var text = System.Console.ReadLine();

    if (text is null || text.Equals("exit", StringComparison.OrdinalIgnoreCase))
    {
        break;
    }

    if (string.IsNullOrWhiteSpace(text))
    {
        continue;
    }

    var actions = await mediationService.HandleMessageAsync(
        session.Id,
        currentParticipant.Id,
        text);

    foreach (var action in actions)
    {
        Render(action, session);
    }

    currentParticipant = currentParticipant.Id == session.ParticipantA.Id
        ? session.ParticipantB
        : session.ParticipantA;
}

static async Task<Session> GetOrCreateSessionAsync(
    IConversationStore store,
    string? configuredSessionId)
{
    if (!string.IsNullOrWhiteSpace(configuredSessionId))
    {
        if (!Guid.TryParse(configuredSessionId, out var sessionId))
        {
            throw new ArgumentException(
                $"'{configuredSessionId}' is not a valid SessionId.",
                nameof(configuredSessionId));
        }

        return await store.GetSessionAsync(sessionId)
            ?? throw new KeyNotFoundException($"Session '{sessionId}' was not found.");
    }

    var participantA = new Participant(Guid.NewGuid(), "A");
    var participantB = new Participant(Guid.NewGuid(), "B");
    var session = new Session(Guid.NewGuid(), participantA, participantB);
    await store.CreateSessionAsync(session);
    return session;
}

static Participant GetNextParticipant(
    Session session,
    IReadOnlyList<Message> history)
{
    var lastIncoming = history.LastOrDefault(message =>
        message.Direction == MessageDirection.ParticipantToMediator);

    return lastIncoming?.AuthorId == session.ParticipantA.Id
        ? session.ParticipantB
        : session.ParticipantA;
}

static void Render(MediatorAction action, Session session)
{
    switch (action)
    {
        case SendToParticipant send:
            var recipient = session.GetParticipant(send.ParticipantId);
            System.Console.WriteLine($"BOT -> {recipient.DisplayName}: {send.Text}");
            break;

        case SendToBoth send:
            System.Console.WriteLine(
                $"BOT -> {session.ParticipantA.DisplayName}: {send.TextForParticipantA}");
            System.Console.WriteLine(
                $"BOT -> {session.ParticipantB.DisplayName}: {send.TextForParticipantB}");
            break;

        case NoAction:
            System.Console.WriteLine("BOT: нет действий");
            break;
    }
}
