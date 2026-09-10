using MediatorBot.Core;
using MediatorBot.Infrastructure;

var participantA = new Participant(Guid.NewGuid(), "A");
var participantB = new Participant(Guid.NewGuid(), "B");
var session = new Session(Guid.NewGuid(), participantA, participantB);

var store = new InMemoryConversationStore([session]);
var modelRuntime = new FakeModelRuntime();
var mediationService = new MediationService(store, modelRuntime);

System.Console.WriteLine("MediatorBot Console. Вводи сообщения по очереди; 'exit' завершает работу.");

var currentParticipant = participantA;
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

    currentParticipant = currentParticipant.Id == participantA.Id
        ? participantB
        : participantA;
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
            System.Console.WriteLine($"BOT -> {session.ParticipantA.DisplayName}: {send.TextForParticipantA}");
            System.Console.WriteLine($"BOT -> {session.ParticipantB.DisplayName}: {send.TextForParticipantB}");
            break;

        case NoAction:
            System.Console.WriteLine("BOT: нет действий");
            break;
    }
}
