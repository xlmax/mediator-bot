using TeleFlow.Annotations;
using TeleFlow.Telegram;

namespace MediatorBot.Telegram;

[ChatType(TelegramChatType.Private)]
[UseFilter<AllowedParticipantFilter>]
public sealed class CommandHandlers(
    TelegramCommandService commandService,
    TelegramInitiativeCommandService initiativeCommandService)
{
    [Command("start")]
    public async Task StartAsync(MessageContext context, CancellationToken cancellationToken)
    {
        var response = await commandService.GetStartAsync(
            GetTelegramUserId(context),
            cancellationToken);
        await context.Message.AnswerAsync(response.Text, cancellationToken);
    }

    [Command("status")]
    public async Task StatusAsync(MessageContext context, CancellationToken cancellationToken)
    {
        var response = await commandService.GetStatusAsync(
            GetTelegramUserId(context),
            cancellationToken);
        await context.Message.AnswerAsync(response.Text, cancellationToken);
    }

    [Command("retry_failed")]
    public async Task RetryFailedAsync(
        MessageContext context,
        CancellationToken cancellationToken)
    {
        var response = await commandService.RetryFailedAsync(
            GetTelegramUserId(context),
            cancellationToken);
        await context.Message.AnswerAsync(response.Text, cancellationToken);
    }

    [Command("proactive_on")]
    public async Task ProactiveOnAsync(
        MessageContext context,
        CancellationToken cancellationToken)
    {
        var response = await initiativeCommandService.EnableAsync(
            GetTelegramUserId(context),
            cancellationToken);
        await context.Message.AnswerAsync(response.Text, cancellationToken);
    }

    [Command("proactive_off")]
    public async Task ProactiveOffAsync(
        MessageContext context,
        CancellationToken cancellationToken)
    {
        var response = await initiativeCommandService.DisableAsync(
            GetTelegramUserId(context),
            cancellationToken);
        await context.Message.AnswerAsync(response.Text, cancellationToken);
    }

    [Command("proactive_status")]
    public async Task ProactiveStatusAsync(
        MessageContext context,
        CancellationToken cancellationToken)
    {
        var response = await initiativeCommandService.GetStatusAsync(
            GetTelegramUserId(context),
            cancellationToken);
        await context.Message.AnswerAsync(response.Text, cancellationToken);
    }

    [Command("help")]
    public async Task HelpAsync(MessageContext context, CancellationToken cancellationToken)
    {
        var response = await commandService.GetHelpAsync(
            GetTelegramUserId(context),
            cancellationToken);
        await context.Message.AnswerAsync(response.Text, cancellationToken);
    }

    private static long GetTelegramUserId(MessageContext context) =>
        context.Sender?.Id
        ?? throw new InvalidOperationException("Telegram message has no sender.");
}
