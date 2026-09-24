using System.Reflection;
using MediatorBot.BehaviorScenarios;
using MediatorBot.Core;
using MediatorBot.Infrastructure;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

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

var apiKey = builder.Configuration["OpenAI:ApiKey"]
    ?? builder.Configuration["OPENAI_API_KEY"];
if (string.IsNullOrWhiteSpace(apiKey))
{
    throw new InvalidOperationException(
        "Configure OpenAI__ApiKey or set the OpenAI:ApiKey user secret for " +
        "MediatorBot.BehaviorScenarios.");
}

var model = RequireConfiguration("OpenAI:Model");
var endpointValue = builder.Configuration["OpenAI:Endpoint"];
if (!string.IsNullOrWhiteSpace(endpointValue) &&
    !Uri.TryCreate(endpointValue, UriKind.Absolute, out _))
{
    throw new InvalidOperationException("OpenAI:Endpoint must be an absolute URI.");
}

var options = new OpenAiModelRuntimeOptions
{
    ApiKey = apiKey,
    Model = model,
    Endpoint = string.IsNullOrWhiteSpace(endpointValue)
        ? null
        : new Uri(endpointValue, UriKind.Absolute),
    MaxOutputTokens = GetPositiveInt("OpenAI:MaxOutputTokens", 1500),
    MaxAttempts = GetPositiveInt("OpenAI:MaxAttempts", 3),
    RequestTimeout = TimeSpan.FromSeconds(
        GetPositiveInt("OpenAI:RequestTimeoutSeconds", 120)),
    RetryBaseDelay = TimeSpan.FromMilliseconds(
        GetPositiveInt("OpenAI:RetryBaseDelayMilliseconds", 1000)),
    RetryMaxDelay = TimeSpan.FromSeconds(
        GetPositiveInt("OpenAI:RetryMaxDelaySeconds", 30))
};
var transcriptDirectory = builder.Configuration["Behavior:TranscriptDirectory"]
    ?? "artifacts/behavioral";
var scenarioNumber = builder.Configuration.GetValue<int?>("Behavior:ScenarioNumber");
var behaviorMode = builder.Configuration["Behavior:Mode"] ?? "Disclosure";
var initiativeOptions = new InitiativeOptions
{
    MinimumReevaluationMinutes = GetPositiveInt(
        "Initiative:MinimumReevaluationMinutes",
        30),
    MaximumReevaluationMinutes = GetPositiveInt(
        "Initiative:MaximumReevaluationMinutes",
        10_080)
};

builder.Services.AddSingleton(options);
builder.Services.AddSingleton(initiativeOptions);
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
builder.Services.AddSingleton<BehaviorScenarioRunner>();
builder.Services.AddSingleton<InitiativeScenarioRunner>();

using var host = builder.Build();
using var cancellation = new CancellationTokenSource();
System.Console.CancelKeyPress += (_, eventArgs) =>
{
    eventArgs.Cancel = true;
    cancellation.Cancel();
};

string transcriptPath;
if (behaviorMode.Equals("Initiative", StringComparison.OrdinalIgnoreCase))
{
    var runner = host.Services.GetRequiredService<InitiativeScenarioRunner>();
    transcriptPath = await runner.RunAsync(
        model,
        transcriptDirectory,
        scenarioNumber,
        cancellation.Token);
}
else if (behaviorMode.Equals("Disclosure", StringComparison.OrdinalIgnoreCase))
{
    var runner = host.Services.GetRequiredService<BehaviorScenarioRunner>();
    transcriptPath = await runner.RunAsync(
        model,
        transcriptDirectory,
        scenarioNumber,
        cancellation.Token);
}
else
{
    throw new InvalidOperationException(
        $"Unknown Behavior:Mode '{behaviorMode}'. Use Disclosure or Initiative.");
}

System.Console.WriteLine($"Behaviour transcript: {transcriptPath}");

string RequireConfiguration(string key)
{
    var value = builder.Configuration[key];
    return string.IsNullOrWhiteSpace(value)
        ? throw new InvalidOperationException($"{key} is not configured.")
        : value;
}

int GetPositiveInt(string key, int defaultValue)
{
    var value = builder.Configuration.GetValue<int?>(key) ?? defaultValue;
    ArgumentOutOfRangeException.ThrowIfNegativeOrZero(value, key);
    return value;
}
