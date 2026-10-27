using System.Security.Claims;
using ActivityTracker.Models;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;

namespace ActivityTracker.Services;
public static class Accounts
{
    public static bool TestLoginEnabled(IConfiguration config, IWebHostEnvironment environment) =>
        config.GetValue<bool?>("TestLogin:Enabled") ?? environment.IsDevelopment();

    public static bool IsAdmin(ClaimsPrincipal user, IConfiguration config) =>
        user.Identity?.IsAuthenticated == true &&
        int.TryParse(config["Admin:ParticipantId"], out var adminId) && adminId > 0 &&
        user.FindFirstValue(ClaimTypes.NameIdentifier) == adminId.ToString();

    public static Task SignInAsync(HttpContext context, Participant participant) =>
        context.SignInAsync(CookieAuthenticationDefaults.AuthenticationScheme,
            new ClaimsPrincipal(new ClaimsIdentity(new[] {
                new Claim(ClaimTypes.NameIdentifier, participant.Id.ToString()),
                new Claim(ClaimTypes.Name, participant.DisplayName)
            }, CookieAuthenticationDefaults.AuthenticationScheme)),
            new AuthenticationProperties { IsPersistent = false, AllowRefresh = true });
}
