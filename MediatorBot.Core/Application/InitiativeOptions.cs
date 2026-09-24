namespace MediatorBot.Core;

public sealed class InitiativeOptions
{
    public bool Enabled { get; init; }

    public bool ShadowMode { get; init; }

    public TimeSpan HeartbeatInterval { get; init; } = TimeSpan.FromMinutes(30);

    public TimeSpan MinimumQuietPeriod { get; init; } = TimeSpan.FromMinutes(30);

    public int MaxHistoryMessages { get; init; } = 80;

    public int RecentDecisionCount { get; init; } = 20;

    public int MinimumReevaluationMinutes { get; init; } = 30;

    public int MaximumReevaluationMinutes { get; init; } = 10_080;

    public int MaxContactsPerParticipantPer24Hours { get; init; } = 2;

    public int MaxDeliveryAttempts { get; init; } = 3;

    public int QuietHoursStartHour { get; init; } = 22;

    public int QuietHoursEndHour { get; init; } = 9;

    public string TimeZoneId { get; init; } = "Europe/Moscow";
}
