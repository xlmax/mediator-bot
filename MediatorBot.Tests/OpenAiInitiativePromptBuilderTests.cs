using MediatorBot.Infrastructure;

namespace MediatorBot.Tests;

public sealed class OpenAiInitiativePromptBuilderTests
{
    [Fact]
    public void SystemPrompt_DefinesSelectiveTemporalAndSafetyPolicy()
    {
        var prompt = OpenAiInitiativePromptBuilder.SystemPrompt;

        Assert.Contains("У тебя нет текущего автора", prompt);
        Assert.Contains("даже в спокойный период", prompt, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("не превращай это в календарный опрос", prompt);
        Assert.Contains("Игнор не доказывает", prompt);
        Assert.Contains("raw private content", prompt);
        Assert.Contains("не связывайся инициативно с предполагаемым источником опасности", prompt);
        Assert.Contains("не является автоматически запретом", prompt);
        Assert.Contains(
            "не продлевай паузу повторно",
            prompt,
            StringComparison.OrdinalIgnoreCase);
        Assert.Contains("не автоматическую отправку", prompt);
        Assert.Contains("ровно один вызов record_initiative_decision", prompt);
    }
}
