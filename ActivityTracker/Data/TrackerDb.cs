using ActivityTracker.Models;
using Microsoft.EntityFrameworkCore;

namespace ActivityTracker.Data;
public sealed class TrackerDb(DbContextOptions<TrackerDb> options) : DbContext(options)
{
    public DbSet<Participant> Participants => Set<Participant>();
    public DbSet<Activity> Activities => Set<Activity>();
    public DbSet<Challenge> Challenges => Set<Challenge>();
    public DbSet<LeaderboardExclusion> LeaderboardExclusions => Set<LeaderboardExclusion>();
    protected override void OnModelCreating(ModelBuilder b)
    {
        b.Entity<Participant>().HasIndex(x => x.StravaAthleteId).IsUnique();
        b.Entity<Participant>().HasIndex(x => x.NormalizedEmail).IsUnique();
        b.Entity<Participant>().Property(x => x.NormalizedEmail).HasMaxLength(254);
        b.Entity<Participant>().Property(x => x.DisplayName).HasMaxLength(80);
        b.Entity<Challenge>().Property(x => x.Name).HasMaxLength(100);
        b.Entity<Challenge>().Property(x => x.Description).HasMaxLength(1000);
        b.Entity<LeaderboardExclusion>().HasKey(x => new { x.ChallengeId, x.ParticipantId });
        b.Entity<LeaderboardExclusion>().HasOne(x => x.Challenge).WithMany().HasForeignKey(x => x.ChallengeId);
        b.Entity<LeaderboardExclusion>().HasOne(x => x.Participant).WithMany().HasForeignKey(x => x.ParticipantId);
        b.Entity<Activity>().Property(x => x.StartLocal).HasColumnType("timestamp without time zone");
        b.Entity<Activity>().HasIndex(x => x.StravaActivityId).IsUnique();
        b.Entity<Activity>().HasIndex(x => new { x.ParticipantId, x.StartLocal });
        b.Entity<Challenge>().Property(x => x.StartsOn).HasColumnType("date");
        b.Entity<Challenge>().Property(x => x.EndsOn).HasColumnType("date");
    }
}
