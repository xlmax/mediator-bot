namespace MediatorBot.Core;

public sealed class InitiativeEvaluationService(
    IInitiativeContextBuilder contextBuilder,
    IInitiativeRuntime runtime,
    IInitiativeStore initiativeStore,
    IExternalTurnQueueStore turnQueueStore,
    ISessionTurnCoordinator turnCoordinator,
    InitiativeOptions options)
{
    public async Task<InitiativeDecision?> TryEvaluateAsync(
        Guid sessionId,
        DateTimeOffset now,
        CancellationToken cancellationToken = default)
    {
        var snapshot = await turnCoordinator.ExecuteAsync(
            sessionId,
            token => PrepareSnapshotAsync(sessionId, now, token),
            cancellationToken);
        if (snapshot is null)
        {
            return null;
        }

        var proposal = await runtime.EvaluateAsync(snapshot.Context, cancellationToken);
        return await turnCoordinator.ExecuteAsync(
            sessionId,
            token => FinalizeAsync(snapshot, proposal, now, token),
            cancellationToken);
    }

    public bool IsQuietHours(DateTimeOffset now)
    {
        var zone = TimeZoneInfo.FindSystemTimeZoneById(options.TimeZoneId);
        var hour = TimeZoneInfo.ConvertTime(now, zone).Hour;
        return options.QuietHoursStartHour < options.QuietHoursEndHour
            ? hour >= options.QuietHoursStartHour && hour < options.QuietHoursEndHour
            : hour >= options.QuietHoursStartHour || hour < options.QuietHoursEndHour;
    }

    private async Task<InitiativeSnapshot?> PrepareSnapshotAsync(
        Guid sessionId,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        if (IsQuietHours(now) ||
            (await turnQueueStore.GetPendingAsync(sessionId, cancellationToken)).Count > 0)
        {
            return null;
        }

        var context = await contextBuilder.BuildAsync(sessionId, now, cancellationToken);
        if (context is null || now - context.LatestParticipantMessage.CreatedAt <
            options.MinimumQuietPeriod)
        {
            return null;
        }

        var latestDecision = context.RecentDecisions.LastOrDefault();
        if (latestDecision is not null &&
            latestDecision.ObservedParticipantMessageId ==
                context.LatestParticipantMessage.Id &&
            latestDecision.NextEvaluationAt > now)
        {
            return null;
        }

        return new InitiativeSnapshot(context, latestDecision?.Id);
    }

    private async Task<InitiativeDecision?> FinalizeAsync(
        InitiativeSnapshot snapshot,
        InitiativeProposal proposal,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        if ((await turnQueueStore.GetPendingAsync(
                snapshot.Context.Session.Id,
                cancellationToken)).Count > 0)
        {
            return null;
        }

        var latestParticipantMessageId = await initiativeStore
            .GetLatestParticipantMessageIdAsync(
                snapshot.Context.Session.Id,
                cancellationToken);
        var latestDecision = await initiativeStore.GetLatestDecisionAsync(
            snapshot.Context.Session.Id,
            cancellationToken);
        if (latestParticipantMessageId != snapshot.Context.LatestParticipantMessage.Id ||
            latestDecision?.Id != snapshot.ExpectedLatestDecisionId)
        {
            return null;
        }

        ValidateProposal(snapshot.Context.Session, proposal);
        var reevaluateMinutes = Math.Clamp(
            proposal.ReevaluateAfterMinutes,
            options.MinimumReevaluationMinutes,
            options.MaximumReevaluationMinutes);
        var status = await DetermineStatusAsync(
            snapshot.Context,
            proposal,
            now,
            cancellationToken);
        var alreadyAppliedSpaceRequest = snapshot.Context.RecentDecisions.Any(
            previous =>
                previous.ObservedParticipantMessageId ==
                    snapshot.Context.LatestParticipantMessage.Id &&
                previous.ReasonCode == InitiativeReasonCode.RequestedSpace);
        var pauseParticipantId = alreadyAppliedSpaceRequest
            ? null
            : proposal.PauseParticipantId;
        DateTimeOffset? pauseUntil = pauseParticipantId is not null &&
                         proposal.PauseForMinutes is int pauseMinutes
            ? now.AddMinutes(Math.Clamp(
                pauseMinutes,
                options.MinimumReevaluationMinutes,
                options.MaximumReevaluationMinutes))
            : null;
        var decision = new InitiativeDecision(
            Guid.NewGuid(),
            snapshot.Context.Session.Id,
            snapshot.Context.LatestParticipantMessage.Id,
            now,
            proposal.Phase,
            proposal.Confidence,
            proposal.DecisionKind,
            proposal.TargetParticipantId,
            proposal.Intent,
            proposal.ReasonCode,
            proposal.OperationalRationale.Trim(),
            proposal.TextForParticipantA,
            proposal.TextForParticipantB,
            now.AddMinutes(reevaluateMinutes),
            status,
            PauseParticipantId: pauseParticipantId,
            PauseUntil: pauseUntil);
        var saved = await initiativeStore.TrySaveDecisionAsync(
            decision,
            snapshot.ExpectedLatestDecisionId,
            cancellationToken);
        if (!saved)
        {
            return null;
        }

        return decision;
    }

    private async Task<InitiativeDecisionStatus> DetermineStatusAsync(
        InitiativeContext context,
        InitiativeProposal proposal,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        if (proposal.DecisionKind is InitiativeDecisionKind.NoAction or
            InitiativeDecisionKind.ReevaluateLater)
        {
            return InitiativeDecisionStatus.NoDelivery;
        }

        if (options.ShadowMode)
        {
            return InitiativeDecisionStatus.Shadow;
        }

        var targets = proposal.DecisionKind == InitiativeDecisionKind.ContactBoth
            ? new[] { context.ParticipantA.Id, context.ParticipantB.Id }
            : new[] { proposal.TargetParticipantId!.Value };
        foreach (var participantId in targets)
        {
            var preference = context.ParticipantPreferences.Single(
                item => item.ParticipantId == participantId);
            var deliveredCount = await initiativeStore.CountDeliveredContactsAsync(
                context.Session.Id,
                participantId,
                now.AddHours(-24),
                cancellationToken);
            if (!preference.AllowsContact(now) ||
                deliveredCount >= options.MaxContactsPerParticipantPer24Hours)
            {
                return InitiativeDecisionStatus.Suppressed;
            }
        }

        return InitiativeDecisionStatus.PendingDelivery;
    }

    private static void ValidateProposal(Session session, InitiativeProposal proposal)
    {
        if (string.IsNullOrWhiteSpace(proposal.OperationalRationale) ||
            proposal.OperationalRationale.Length > 500)
        {
            throw new InvalidDataException(
                "Initiative operational rationale must contain at most 500 characters.");
        }

        var isParticipant = proposal.TargetParticipantId is Guid target &&
            (target == session.ParticipantA.Id || target == session.ParticipantB.Id);
        switch (proposal.DecisionKind)
        {
            case InitiativeDecisionKind.ContactParticipant when !isParticipant:
                throw new InvalidDataException(
                    "ContactParticipant requires a session participant target.");
            case InitiativeDecisionKind.ContactParticipant:
                {
                    var targetIsA = proposal.TargetParticipantId == session.ParticipantA.Id;
                    var targetText = targetIsA
                        ? proposal.TextForParticipantA
                        : proposal.TextForParticipantB;
                    var nonTargetText = targetIsA
                        ? proposal.TextForParticipantB
                        : proposal.TextForParticipantA;
                    if (string.IsNullOrWhiteSpace(targetText) ||
                        !string.IsNullOrWhiteSpace(nonTargetText))
                    {
                        throw new InvalidDataException(
                            "ContactParticipant requires text only for its target.");
                    }

                    break;
                }

            case InitiativeDecisionKind.ContactBoth when
                proposal.TargetParticipantId is not null ||
                string.IsNullOrWhiteSpace(proposal.TextForParticipantA) ||
                string.IsNullOrWhiteSpace(proposal.TextForParticipantB):
                throw new InvalidDataException(
                    "ContactBoth requires no target id and text for both participants.");
            case InitiativeDecisionKind.NoAction or
                InitiativeDecisionKind.ReevaluateLater when
                proposal.TargetParticipantId is not null ||
                !string.IsNullOrWhiteSpace(proposal.TextForParticipantA) ||
                !string.IsNullOrWhiteSpace(proposal.TextForParticipantB):
                throw new InvalidDataException(
                    "A non-contact decision cannot contain a target or message text.");
            case InitiativeDecisionKind.NoAction or
                InitiativeDecisionKind.ReevaluateLater:
                break;
        }

        if (proposal.PauseParticipantId is Guid pauseTarget &&
            pauseTarget != session.ParticipantA.Id &&
            pauseTarget != session.ParticipantB.Id)
        {
            throw new InvalidDataException(
                "A contact pause can target only a session participant.");
        }

        if (proposal.PauseParticipantId.HasValue != proposal.PauseForMinutes.HasValue)
        {
            throw new InvalidDataException(
                "A contact pause requires both participant and duration.");
        }

        if (proposal.PauseParticipantId is not null &&
            proposal.DecisionKind is not (
                InitiativeDecisionKind.NoAction or
                InitiativeDecisionKind.ReevaluateLater))
        {
            throw new InvalidDataException(
                "A temporary contact pause cannot be combined with contact.");
        }
    }

    private sealed record InitiativeSnapshot(
        InitiativeContext Context,
        Guid? ExpectedLatestDecisionId);
}
