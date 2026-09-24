using MediatorBot.Core;
using MediatorBot.Infrastructure;
using MediatorBot.Telegram;

namespace MediatorBot.Tests;

public sealed class TelegramInitiativeCommandServiceTests
{
    [Fact]
    public async Task Participant_CanDisableAndReenableProactiveMessages()
    {
        var participantA = new Participant(Guid.NewGuid(), "A");
        var participantB = new Participant(Guid.NewGuid(), "B");
        var session = new Session(Guid.NewGuid(), participantA, participantB);
        var store = new InMemoryConversationStore([session]);
        var adapterOptions = new TelegramAdapterOptions
        {
            SessionId = session.Id,
            ParticipantAUserId = 10001,
            ParticipantBUserId = 10002,
            ModelDisplayName = "test"
        };
        var registry = new TelegramParticipantRegistry(store, store, adapterOptions);
        await registry.InitializeAsync();
        var service = new TelegramInitiativeCommandService(
            registry,
            store,
            new InitiativeOptions { Enabled = true });

        var disabled = await service.DisableAsync(10001);
        var disabledStatus = await service.GetStatusAsync(10001);
        var enabled = await service.EnableAsync(10001);
        var enabledStatus = await service.GetStatusAsync(10001);

        Assert.Contains("отключены", disabled.Text);
        Assert.Contains("отключены", disabledStatus.Text);
        Assert.Contains("снова разрешены", enabled.Text);
        Assert.Contains("разрешены", enabledStatus.Text);
        var preferences = await store.GetParticipantPreferencesAsync(session);
        Assert.True(preferences.Single(item =>
            item.ParticipantId == participantA.Id).IsEnabled);
    }
}
