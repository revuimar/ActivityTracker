using System.ComponentModel.DataAnnotations;
using System.Threading.RateLimiting;
using ActivityTracker.Data;
using ActivityTracker.Models;
using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;

namespace ActivityTracker.Services;
public static class AccountEndpoints
{
    public static void MapAccounts(this WebApplication app)
    {
        app.MapPost("/auth/register", async (HttpContext ctx, IAntiforgery csrf,
            IDbContextFactory<TrackerDb> factory, IPasswordHasher<Participant> hasher) => {
            if (!Accounts.TestLoginEnabled(app.Configuration, app.Environment)) return Results.NotFound();
            if (app.Environment.IsProduction() && !ctx.Request.IsHttps) return Results.BadRequest("Test login requires HTTPS in production.");
            try { await csrf.ValidateRequestAsync(ctx); }
            catch (AntiforgeryValidationException) { return Results.BadRequest("Invalid form token. Reload the page."); }
            var form = await ctx.Request.ReadFormAsync();
            var email = form["email"].ToString().Trim().ToUpperInvariant();
            var name = form["name"].ToString().Trim();
            var password = form["password"].ToString();
            if (email.Length > 254 || !new EmailAddressAttribute().IsValid(email) ||
                name.Length is < 1 or > 80 || password.Length is < 12 or > 128)
                return Results.Redirect("/login?error=registration");
            await using var db = await factory.CreateDbContextAsync();
            var person = new Participant { NormalizedEmail = email, DisplayName = name };
            person.PasswordHash = hasher.HashPassword(person, password);
            db.Participants.Add(person);
            try { await db.SaveChangesAsync(); }
            catch (DbUpdateException ex) when (ex.InnerException is Npgsql.PostgresException { SqlState: "23505" }) {
                return Results.Redirect("/login?error=registration");
            }
            await Accounts.SignInAsync(ctx, person);
            return Results.Redirect("/");
        }).RequireRateLimiting("accounts");

        app.MapPost("/auth/login", async (HttpContext ctx, IAntiforgery csrf,
            IDbContextFactory<TrackerDb> factory, IPasswordHasher<Participant> hasher) => {
            if (!Accounts.TestLoginEnabled(app.Configuration, app.Environment)) return Results.NotFound();
            if (app.Environment.IsProduction() && !ctx.Request.IsHttps) return Results.BadRequest("Test login requires HTTPS in production.");
            try { await csrf.ValidateRequestAsync(ctx); }
            catch (AntiforgeryValidationException) { return Results.BadRequest("Invalid form token. Reload the page."); }
            var form = await ctx.Request.ReadFormAsync();
            var email = form["email"].ToString().Trim().ToUpperInvariant();
            var password = form["password"].ToString();
            if (email.Length > 254 || password.Length > 128) return Results.Redirect("/login?error=login");
            await using var db = await factory.CreateDbContextAsync();
            var person = await db.Participants.SingleOrDefaultAsync(p => p.NormalizedEmail == email);
            // Perform password verification even for unknown accounts to avoid an easy timing oracle.
            var result = hasher.VerifyHashedPassword(person ?? new Participant(),
                person?.PasswordHash ?? app.Services.GetRequiredService<DummyPassword>().Hash, password);
            if (person?.PasswordHash is null || result == PasswordVerificationResult.Failed)
                return Results.Redirect("/login?error=login");
            if (result == PasswordVerificationResult.SuccessRehashNeeded) {
                person.PasswordHash = hasher.HashPassword(person, password);
                await db.SaveChangesAsync();
            }
            await Accounts.SignInAsync(ctx, person);
            return Results.Redirect("/");
        }).RequireRateLimiting("accounts");

        app.MapPost("/auth/logout", async (HttpContext ctx, IAntiforgery csrf) => {
            try { await csrf.ValidateRequestAsync(ctx); }
            catch (AntiforgeryValidationException) { return Results.BadRequest("Invalid form token. Reload the page."); }
            await ctx.SignOutAsync();
            return Results.Redirect("/");
        }).RequireAuthorization();
    }
}
public sealed class DummyPassword(IPasswordHasher<Participant> hasher)
{
    public string Hash { get; } = hasher.HashPassword(new Participant(), Guid.NewGuid().ToString());
}
