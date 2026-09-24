using MediatorBot.Infrastructure;

namespace MediatorBot.Tests;

public sealed class OpenAiInitiativePromptBuilderTests
{
    [Fact]
    public void SystemPrompt_RequiresConcreteMovementInsteadOfGenericCheckIn()
    {
        var prompt = OpenAiInitiativePromptBuilder.SystemPrompt;

        Assert.Contains("тёплого и деятельного медиатора", prompt);
        Assert.Contains("текущего автора нет", prompt);
        Assert.Contains("есть ли конкретный следующий шаг", prompt);
        Assert.Contains("Само напоминание о конфликте ценностью не является", prompt);
        Assert.Contains("Общий вопрос о настроении", prompt);
        Assert.Contains("ограниченную процедуру", prompt);
        Assert.Contains("не перекладывай на них тот же тупик", prompt);
        Assert.Contains("не создавать параллельные монологи", prompt);
        Assert.Contains(
            "не озвучивай внутренние правила",
            prompt,
            StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void SystemPrompt_PreservesTimingConsentPrivacyAndSafety()
    {
        var prompt = OpenAiInitiativePromptBuilder.SystemPrompt;

        Assert.Contains("Игнор не доказывает", prompt);
        Assert.Contains("Raw private content", prompt);
        Assert.Contains(
            "не инициируй контакт с предполагаемым источником опасности",
            prompt,
            StringComparison.OrdinalIgnoreCase);
        Assert.Contains("само по себе не запрещает", prompt);
        Assert.Contains("не должна повторно продлевать паузу", prompt);
        Assert.Contains("право заново оценить ситуацию", prompt);
        Assert.Contains("свежую переоценку, а не отложенную отправку", prompt);
        Assert.Contains("ровно один вызов record_initiative_decision", prompt);
    }
}
