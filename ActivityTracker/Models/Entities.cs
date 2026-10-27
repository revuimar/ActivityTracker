namespace ActivityTracker.Models;

public class Participant
{
    public int Id { get; set; }
    public long? StravaAthleteId { get; set; }
    public string? NormalizedEmail { get; set; }
    public string? PasswordHash { get; set; }
    public string DisplayName { get; set; } = "";
    public string? AccessToken { get; set; } // Protected with Data Protection; future sync must unprotect with StravaTokens.v1
    public string? RefreshToken { get; set; }
    public DateTimeOffset? TokenExpiresAt { get; set; }
    public List<Activity> Activities { get; set; } = [];
}
public class Challenge
{
    public int Id { get; set; }
    public string Name { get; set; } = "Morning Challenge";
    public string Description { get; set; } = "";
    public DateOnly StartsOn { get; set; }
    public DateOnly EndsOn { get; set; }
    public TimeOnly Cutoff { get; set; } = new(11, 0);
    public int MinimumMinutes { get; set; } = 15;
}
public class Activity
{
    public long Id { get; set; }
    public long StravaActivityId { get; set; }
    public int ParticipantId { get; set; }
    public Participant Participant { get; set; } = null!;
    public DateTimeOffset StartUtc { get; set; }
    public DateTime StartLocal { get; set; } // Strava local clock time, unspecified kind
    public int ElapsedSeconds { get; set; }
    public int MovingSeconds { get; set; }
    public string SportType { get; set; } = "";
}

// A missing row means included, so new accounts join all existing/future leaderboards.
public class LeaderboardExclusion
{
    public int ChallengeId { get; set; }
    public Challenge Challenge { get; set; } = null!;
    public int ParticipantId { get; set; }
    public Participant Participant { get; set; } = null!;
}
