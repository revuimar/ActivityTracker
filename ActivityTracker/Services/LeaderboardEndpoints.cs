using ActivityTracker.Data;
using ActivityTracker.Models;
using Microsoft.AspNetCore.Antiforgery;
using Microsoft.EntityFrameworkCore;
using System.Globalization;

namespace ActivityTracker.Services;
public static class LeaderboardEndpoints
{
    public static void MapLeaderboards(this WebApplication app)
    {
        app.MapPost("/leaderboards/save", async (HttpContext ctx, IAntiforgery csrf, IDbContextFactory<TrackerDb> factory) => {
            try { await csrf.ValidateRequestAsync(ctx); }
            catch (AntiforgeryValidationException) { return Results.BadRequest("Invalid form token. Reload the page."); }
            var form = await ctx.Request.ReadFormAsync();
            if (!int.TryParse(form["id"], out var id) || id < 0) return Results.BadRequest("Invalid leaderboard.");
            var name = form["name"].ToString().Trim();
            var description = form["description"].ToString().Trim();
            var valid = name.Length is >= 1 and <= 100 && description.Length <= 1000;
            valid &= DateOnly.TryParseExact(form["startsOn"], "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var starts);
            valid &= DateOnly.TryParseExact(form["endsOn"], "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var ends);
            valid &= TimeOnly.TryParseExact(form["cutoff"], "HH:mm", CultureInfo.InvariantCulture, DateTimeStyles.None, out var cutoff);
            valid &= int.TryParse(form["minimumMinutes"], out var minutes) && minutes is >= 15 and <= 1440;
            if (!valid || ends < starts) return Results.BadRequest("Check name, dates, cutoff and minimum duration (15–1440 minutes). Go back to correct the form.");
            await using var db = await factory.CreateDbContextAsync();
            var board = id == 0 ? new Challenge() : await db.Challenges.FindAsync(id);
            if (board is null) return Results.NotFound();
            if (id == 0) db.Challenges.Add(board);
            board.Name = name; board.Description = description;
            board.StartsOn = starts; board.EndsOn = ends; board.Cutoff = cutoff; board.MinimumMinutes = minutes;
            await db.SaveChangesAsync();
            return Results.Redirect($"/leaderboards/{board.Id}");
        }).RequireAuthorization();

        app.MapPost("/leaderboards/{id:int}/members/update", async (int id, HttpContext ctx, IAntiforgery csrf, IDbContextFactory<TrackerDb> factory) => {
            try { await csrf.ValidateRequestAsync(ctx); }
            catch (AntiforgeryValidationException) { return Results.BadRequest("Invalid form token. Reload the page."); }
            var form = await ctx.Request.ReadFormAsync();
            if (!int.TryParse(form["participantId"], out var personId) ||
                (form["action"] != "include" && form["action"] != "exclude")) return Results.BadRequest();
            await using var db = await factory.CreateDbContextAsync();
            if (!await db.Challenges.AnyAsync(x => x.Id == id) || !await db.Participants.AnyAsync(x => x.Id == personId))
                return Results.NotFound();
            if (form["action"] == "exclude") {
                // ON CONFLICT makes repeat clicks and concurrent admin requests idempotent.
                await db.Database.ExecuteSqlInterpolatedAsync($"INSERT INTO \"LeaderboardExclusions\" (\"ChallengeId\", \"ParticipantId\") VALUES ({id}, {personId}) ON CONFLICT DO NOTHING");
            } else {
                await db.LeaderboardExclusions.Where(x => x.ChallengeId == id && x.ParticipantId == personId).ExecuteDeleteAsync();
            }
            return Results.Redirect($"/leaderboards/{id}/members");
        }).RequireAuthorization("Admin");
    }
}
