using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.HttpOverrides;
using System.Net;
using System.Net.Http.Headers;
using System.Security.Claims;
using Microsoft.AspNetCore.Authentication;
using System.Security.Cryptography;
using System.Text.Json;
using ActivityTracker.Components;
using ActivityTracker.Data;
using ActivityTracker.Models;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.EntityFrameworkCore;

var builder = WebApplication.CreateBuilder(args);
builder.Services.AddRazorComponents();
builder.Services.AddDataProtection().SetApplicationName("ActivityTracker");
var keyDirectory = builder.Configuration["DataProtection:KeyDirectory"];
if (!string.IsNullOrWhiteSpace(keyDirectory))
    builder.Services.AddDataProtection().SetApplicationName("ActivityTracker")
        .PersistKeysToFileSystem(new DirectoryInfo(keyDirectory));
builder.Services.Configure<ForwardedHeadersOptions>(options => {
    options.ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto;
    // Trust only a proxy explicitly configured by the server administrator.
    var proxy = builder.Configuration["ReverseProxy:KnownProxy"];
    if (!string.IsNullOrWhiteSpace(proxy)) options.KnownProxies.Add(IPAddress.Parse(proxy));
});
builder.Services.AddDbContextFactory<TrackerDb>(o => o.UseNpgsql(builder.Configuration.GetConnectionString("Default")));
builder.Services.AddHttpClient("strava", c => c.BaseAddress = new Uri("https://www.strava.com/"));
builder.Services.AddAuthentication(CookieAuthenticationDefaults.AuthenticationScheme).AddCookie(o => {
    o.Cookie.HttpOnly = true;
    o.Cookie.SameSite = SameSiteMode.Lax;
    o.Cookie.SecurePolicy = builder.Environment.IsProduction() ? CookieSecurePolicy.Always : CookieSecurePolicy.SameAsRequest;
});
builder.Services.AddAuthorization();
var app = builder.Build();
app.UseForwardedHeaders();
app.UseStaticFiles();
app.UseAuthentication();
app.UseAuthorization();
app.UseAntiforgery();

app.MapGet("/health", async (IDbContextFactory<TrackerDb> factory) => {
    try {
        await using var db = await factory.CreateDbContextAsync();
        return await db.Database.CanConnectAsync()
            ? Results.Ok(new { status = "ok" }) : Results.StatusCode(503);
    } catch { return Results.StatusCode(503); }
});
app.MapGet("/auth/strava/start", (HttpContext ctx, IConfiguration config) => {
    if (app.Environment.IsProduction() && !ctx.Request.IsHttps)
        return Results.Problem("Strava login requires HTTPS through a configured trusted proxy.", statusCode: 503);
    var id = config["Strava:ClientId"];
    var redirect = config["Strava:RedirectUri"];
    if (string.IsNullOrWhiteSpace(id) || string.IsNullOrWhiteSpace(redirect))
        return Results.Problem("Configure STRAVA_CLIENT_ID and STRAVA_REDIRECT_URI first.", statusCode: 503);
    var state = Convert.ToHexString(RandomNumberGenerator.GetBytes(32));
    ctx.Response.Cookies.Append("strava_oauth_state", state, new CookieOptions {
        HttpOnly = true, Secure = ctx.Request.IsHttps, SameSite = SameSiteMode.Lax, MaxAge = TimeSpan.FromMinutes(10)
    });
    var url = $"https://www.strava.com/oauth/authorize?client_id={Uri.EscapeDataString(id)}&response_type=code&redirect_uri={Uri.EscapeDataString(redirect)}&approval_prompt=auto&scope=read,activity:read&state={state}";
    return Results.Redirect(url);
});
app.MapGet("/auth/strava/callback", async (HttpContext ctx, string? code, string? state, string? error, IConfiguration config, IHttpClientFactory httpFactory, IDbContextFactory<TrackerDb> factory, IDataProtectionProvider protection) => {
    if (error is not null) return Results.BadRequest("Strava authorization declined: " + error);
    var cookieState = ctx.Request.Cookies["strava_oauth_state"];
    ctx.Response.Cookies.Delete("strava_oauth_state");
    if (string.IsNullOrEmpty(code) || string.IsNullOrEmpty(state) || string.IsNullOrEmpty(cookieState) ||
        !CryptographicOperations.FixedTimeEquals(System.Text.Encoding.UTF8.GetBytes(state), System.Text.Encoding.UTF8.GetBytes(cookieState)))
        return Results.BadRequest("Invalid OAuth state or authorization code.");
    var client = httpFactory.CreateClient("strava");
    var response = await client.PostAsync("oauth/token", new FormUrlEncodedContent(new Dictionary<string,string> {
        ["client_id"] = config["Strava:ClientId"] ?? "",
        ["client_secret"] = config["Strava:ClientSecret"] ?? "",
        ["code"] = code, ["grant_type"] = "authorization_code"
    }));
    if (!response.IsSuccessStatusCode) return Results.Problem("Strava token exchange failed.", statusCode: 502);
    using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
    var root = json.RootElement;
    var athlete = root.GetProperty("athlete");
    var athleteId = athlete.GetProperty("id").GetInt64();
    var first = athlete.TryGetProperty("firstname", out var f) ? f.GetString() : "";
    var last = athlete.TryGetProperty("lastname", out var l) ? l.GetString() : "";
    await using var db = await factory.CreateDbContextAsync();
    var person = await db.Participants.SingleOrDefaultAsync(p => p.StravaAthleteId == athleteId);
    if (person is null) { person = new Participant { StravaAthleteId = athleteId }; db.Participants.Add(person); }
    person.DisplayName = ($"{first} {last}").Trim();
    var protector = protection.CreateProtector("StravaTokens.v1");
    person.AccessToken = protector.Protect(root.GetProperty("access_token").GetString()!);
    person.RefreshToken = protector.Protect(root.GetProperty("refresh_token").GetString()!);
    person.TokenExpiresAt = DateTimeOffset.FromUnixTimeSeconds(root.GetProperty("expires_at").GetInt64());
    await db.SaveChangesAsync();
    var identity = new ClaimsIdentity(new[] { new Claim(ClaimTypes.NameIdentifier, athleteId.ToString()), new Claim(ClaimTypes.Name, person.DisplayName) }, CookieAuthenticationDefaults.AuthenticationScheme);
    await ctx.SignInAsync(CookieAuthenticationDefaults.AuthenticationScheme, new ClaimsPrincipal(identity));
    return Results.Redirect("/");
});
app.MapRazorComponents<App>();

// MVP bootstrap: creates the schema on first run. Replace with EF migrations before evolving production data.
using (var scope = app.Services.CreateScope()) {
    var factory = scope.ServiceProvider.GetRequiredService<IDbContextFactory<TrackerDb>>();
    for (int attempt=0; attempt<15; attempt++) {
        try { await using var db = await factory.CreateDbContextAsync(); await db.Database.EnsureCreatedAsync(); break; }
        catch (Npgsql.NpgsqlException) when (attempt < 14) { await Task.Delay(2000); }
    }
}
app.Run();
