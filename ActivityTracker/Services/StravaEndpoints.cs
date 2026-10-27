using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using ActivityTracker.Data;
using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace ActivityTracker.Services;

public sealed record StravaEvent(
    [property: JsonPropertyName("subscription_id")] long SubscriptionId,
    [property: JsonPropertyName("owner_id")] long OwnerId,
    [property: JsonPropertyName("object_id")] long ObjectId,
    [property: JsonPropertyName("object_type")] string ObjectType,
    [property: JsonPropertyName("aspect_type")] string AspectType,
    [property: JsonPropertyName("updates")] JsonElement Updates);

public static class StravaEndpoints
{
    public static bool HasActivityScope(string? scope) => (scope ?? "").Split(',', StringSplitOptions.TrimEntries)
        .Any(s => s is "activity:read" or "activity:read_all");

    public static void MapStravaSync(this WebApplication app)
    {
        app.MapGet("/webhooks/strava", (HttpContext ctx, IConfiguration config) => {
            var token = config["Strava:WebhookVerifyToken"];
            if (string.IsNullOrEmpty(token) || token.Length < 32) return Results.StatusCode(503);
            var supplied = ctx.Request.Query["hub.verify_token"].ToString();
            if (ctx.Request.Query["hub.mode"] != "subscribe" ||
                !CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(token), Encoding.UTF8.GetBytes(supplied)))
                return Results.StatusCode(403);
            var challenge = ctx.Request.Query["hub.challenge"].ToString();
            if (challenge.Length is < 1 or > 512) return Results.BadRequest();
            return Results.Json(new Dictionary<string, string> { ["hub.challenge"] = challenge });
        }).RequireRateLimiting("strava-webhook");

        app.MapPost("/webhooks/strava", async (HttpContext ctx, IConfiguration config, IDbContextFactory<TrackerDb> factory) => {
            var subscriptionId = config.GetValue<long?>("Strava:WebhookSubscriptionId") ?? 0;
            if (subscriptionId <= 0) return Results.StatusCode(503);
            if (ctx.Request.ContentLength > 16384) return Results.StatusCode(413);
            StravaEvent? notification;
            try { notification = await ctx.Request.ReadFromJsonAsync<StravaEvent>(ctx.RequestAborted); }
            catch (JsonException) { return Results.BadRequest(); }
            if (notification is null || notification.SubscriptionId != subscriptionId) return Results.StatusCode(403);
            if (notification.OwnerId <= 0 || notification.ObjectId <= 0) return Results.BadRequest();
            if (notification.ObjectType is not ("activity" or "athlete") ||
                notification.AspectType is not ("create" or "update" or "delete")) return Results.Ok();
            if (notification.ObjectType == "athlete" &&
                (notification.AspectType != "update" || notification.Updates.ValueKind != JsonValueKind.Object ||
                 !notification.Updates.TryGetProperty("authorized", out var authorized) || authorized.ToString() != "false"))
                return Results.Ok();
            await using var db = await factory.CreateDbContextAsync(ctx.RequestAborted);
            var personId = await db.Participants.Where(p => p.StravaAthleteId == notification.OwnerId &&
                    p.StravaActivityReadGranted && p.AccessToken != null)
                .Select(p => (int?)p.Id).SingleOrDefaultAsync(ctx.RequestAborted);
            if (personId.HasValue)
                await StravaQueue.EnqueueAsync(db, personId.Value, notification.ObjectType == "activity" ? notification.ObjectId : -1, ctx.RequestAborted);
            // Acknowledge only after durable enqueue; API requests happen in the worker.
            return Results.Ok();
        }).WithMetadata(new RequestSizeLimitAttribute(16384)).RequireRateLimiting("strava-webhook");

        app.MapPost("/strava/sync", async (HttpContext ctx, IAntiforgery csrf, IDbContextFactory<TrackerDb> factory) => {
            try { await csrf.ValidateRequestAsync(ctx); }
            catch (AntiforgeryValidationException) { return Results.BadRequest("Invalid form token. Reload the page."); }
            if (!int.TryParse(ctx.User.FindFirstValue(ClaimTypes.NameIdentifier), out var id)) return Results.Unauthorized();
            await using var db = await factory.CreateDbContextAsync(ctx.RequestAborted);
            var connected = await db.Participants.AnyAsync(p => p.Id == id && p.StravaActivityReadGranted && p.AccessToken != null, ctx.RequestAborted);
            if (!connected) return Results.Redirect("/strava?reconnect=true");
            await StravaQueue.EnqueueAsync(db, id, ct: ctx.RequestAborted);
            return Results.Redirect("/strava");
        }).RequireAuthorization().RequireRateLimiting("strava-sync");
    }
}
