using ActivityTracker.Models;
using Microsoft.EntityFrameworkCore;

namespace ActivityTracker.Data;
public sealed class TrackerDb(DbContextOptions<TrackerDb> options) : DbContext(options)
{
    public DbSet<Participant> Participants => Set<Participant>();
    public DbSet<Activity> Activities => Set<Activity>();
    public DbSet<Challenge> Challenges => Set<Challenge>();
    protected override void OnModelCreating(ModelBuilder b)
    {
        b.Entity<Participant>().HasIndex(x => x.StravaAthleteId).IsUnique();
        b.Entity<Activity>().HasIndex(x => x.StravaActivityId).IsUnique();
        b.Entity<Activity>().HasIndex(x => new { x.ParticipantId, x.StartLocal });
        b.Entity<Challenge>().Property(x => x.StartsOn).HasColumnType("date");
        b.Entity<Challenge>().Property(x => x.EndsOn).HasColumnType("date");
    }
}
