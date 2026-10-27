using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using ActivityTracker.Models;
using Microsoft.AspNetCore.DataProtection;

namespace ActivityTracker.Services;

public sealed class StravaRetryException(TimeSpan delay) : Exception("Strava is temporarily unavailable.")
{
    public TimeSpan Delay { get; } = delay;
}
public sealed class StravaReconnectException : Exception;

// One worker sends API requests; shared cooldown also covers work for other participants.
public sealed class StravaRateGate
{
    public DateTimeOffset BlockedUntil { get; set; }
    public DateTimeOffset LastRequestAt { get; set; }
    public static TimeSpan RateDelay(HttpResponseMessage response, DateTimeOffset now)
    {
        var until = now;
        foreach (var prefix in new[] { "X-RateLimit", "X-ReadRateLimit" }) {
            if (!response.Headers.TryGetValues(prefix + "-Limit", out var limits) ||
                !response.Headers.TryGetValues(prefix + "-Usage", out var usages)) continue;
            var l = string.Join(",", limits).Split(',');
            var u = string.Join(",", usages).Split(',');
            if (l.Length != 2 || u.Length != 2) continue;
            for (var i = 0; i < 2; i++) {
                if (!int.TryParse(l[i], out var limit) || !int.TryParse(u[i], out var usage) || usage < limit) continue;
                var reset = i == 0
                    ? DateTimeOffset.FromUnixTimeSeconds((now.ToUnixTimeSeconds() / 900 + 1) * 900).AddSeconds(2)
                    : new DateTimeOffset(now.UtcDateTime.Date.AddDays(1), TimeSpan.Zero).AddSeconds(2);
                if (reset > until) until = reset;
            }
        }
        var retry = response.Headers.RetryAfter;
        var retryAt = retry?.Date ?? (retry?.Delta is { } delta ? now.Add(delta) : now);
        if (retryAt > until) until = retryAt;
        if (response.StatusCode == HttpStatusCode.TooManyRequests && until <= now) until = now.AddMinutes(15);
        return until - now;
    }
}

public sealed class StravaApi(IHttpClientFactory clients, IDataProtectionProvider protection,
    IConfiguration config, StravaRateGate gate)
{
    private readonly IDataProtector protector = protection.CreateProtector("StravaTokens.v1");

    private async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
    {
        var now = DateTimeOffset.UtcNow;
        if (gate.BlockedUntil > now) throw new StravaRetryException(gate.BlockedUntil - now);
        var spacing = gate.LastRequestAt.AddSeconds(1) - now;
        if (spacing > TimeSpan.Zero) await Task.Delay(spacing, ct);
        gate.LastRequestAt = DateTimeOffset.UtcNow;
        using var client = clients.CreateClient("strava");
        var response = await client.SendAsync(request, ct);
        var delay = StravaRateGate.RateDelay(response, DateTimeOffset.UtcNow);
        if (delay > TimeSpan.Zero) gate.BlockedUntil = DateTimeOffset.UtcNow.Add(delay);
        if (response.StatusCode == HttpStatusCode.TooManyRequests || (int)response.StatusCode >= 500) {
            response.Dispose();
            throw new StravaRetryException(delay > TimeSpan.Zero ? delay : TimeSpan.FromSeconds(30));
        }
        return response;
    }

    private async Task RefreshAsync(Participant person, Func<Task> saveTokens, CancellationToken ct)
    {
        if (string.IsNullOrEmpty(person.RefreshToken)) throw new StravaReconnectException();
        using var request = new HttpRequestMessage(HttpMethod.Post, "oauth/token") {
            Content = new FormUrlEncodedContent(new Dictionary<string, string> {
                ["client_id"] = config["Strava:ClientId"] ?? "",
                ["client_secret"] = config["Strava:ClientSecret"] ?? "",
                ["grant_type"] = "refresh_token",
                ["refresh_token"] = protector.Unprotect(person.RefreshToken)
            })
        };
        using var response = await SendAsync(request, ct);
        if (response.StatusCode is HttpStatusCode.BadRequest or HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden) {
            using var error = JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct));
            if (error.RootElement.TryGetProperty("errors", out var errors) && errors.ValueKind == JsonValueKind.Array &&
                errors.EnumerateArray().Any(e =>
                    ((e.TryGetProperty("resource", out var resource) && resource.GetString() == "RefreshToken") ||
                     (e.TryGetProperty("field", out var field) && field.GetString() == "refresh_token")) &&
                    e.TryGetProperty("code", out var code) && code.GetString() == "invalid"))
                throw new StravaReconnectException();
            // Invalid app credentials are a configuration error, not proof of athlete revocation.
        }
        response.EnsureSuccessStatusCode();
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct));
        var root = document.RootElement;
        person.AccessToken = protector.Protect(root.GetProperty("access_token").GetString()!);
        person.RefreshToken = protector.Protect(root.GetProperty("refresh_token").GetString()!);
        person.TokenExpiresAt = DateTimeOffset.FromUnixTimeSeconds(root.GetProperty("expires_at").GetInt64());
        // Persist rotated refresh tokens BEFORE making another API call or retrying a job.
        await saveTokens();
    }

    public async Task<JsonDocument?> GetAsync(Participant person, string path, Func<Task> saveTokens, CancellationToken ct)
    {
        if (person.AccessToken is null || !person.StravaActivityReadGranted) throw new StravaReconnectException();
        if (person.TokenExpiresAt is null || person.TokenExpiresAt <= DateTimeOffset.UtcNow.AddMinutes(2))
            await RefreshAsync(person, saveTokens, ct);
        for (var attempt = 0; attempt < 2; attempt++) {
            using var request = new HttpRequestMessage(HttpMethod.Get, "api/v3/" + path);
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", protector.Unprotect(person.AccessToken!));
            using var response = await SendAsync(request, ct);
            if (response.StatusCode == HttpStatusCode.Unauthorized) {
                if (attempt == 1) throw new StravaReconnectException();
                await RefreshAsync(person, saveTokens, ct);
                continue;
            }
            if (response.StatusCode == HttpStatusCode.Forbidden) {
                if (path.StartsWith("activities/", StringComparison.Ordinal)) return null;
                throw new StravaReconnectException();
            }
            if (response.StatusCode == HttpStatusCode.NotFound) return null;
            response.EnsureSuccessStatusCode();
            return JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct));
        }
        throw new StravaReconnectException();
    }

    public static Activity ParseActivity(JsonElement json, int participantId)
    {
        var id = json.GetProperty("id").GetInt64();
        var moving = json.GetProperty("moving_time").GetInt32();
        var elapsed = json.GetProperty("elapsed_time").GetInt32();
        if (id <= 0 || moving < 0 || elapsed < 0) throw new JsonException("Invalid activity.");
        return new Activity {
            StravaActivityId = id, ParticipantId = participantId,
            StartUtc = DateTimeOffset.Parse(json.GetProperty("start_date").GetString()!, CultureInfo.InvariantCulture).ToUniversalTime(),
            StartLocal = DateTime.SpecifyKind(DateTime.Parse(json.GetProperty("start_date_local").GetString()!,
                CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind), DateTimeKind.Unspecified),
            MovingSeconds = moving, ElapsedSeconds = elapsed,
            SportType = json.TryGetProperty("sport_type", out var sport) ? sport.GetString() ?? "" : ""
        };
    }

    public static bool IsPrivate(JsonElement json) =>
        (json.TryGetProperty("private", out var value) && value.ValueKind == JsonValueKind.True) ||
        (json.TryGetProperty("visibility", out var visibility) && visibility.GetString() == "only_me");
}
