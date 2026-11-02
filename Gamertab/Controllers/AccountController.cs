using System.Security.Claims;
using Gamertab.Models;
using Gamertab.Services;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;

namespace Gamertab.Controllers;

public class AccountController(UserManager<IdentityUser> users, SignInManager<IdentityUser> signIn, PlanAccess plans) : Controller
{
    [HttpGet]
    public async Task<IActionResult> Register()
    {
        await SetGoogleAvailabilityAsync();
        return View(new RegisterInput());
    }

    [HttpPost]
    public async Task<IActionResult> Register(RegisterInput input)
    {
        await SetGoogleAvailabilityAsync();
        if (!ModelState.IsValid)
            return View(input);
        var user = new IdentityUser { UserName = input.Email.Trim(), Email = input.Email.Trim() };
        var result = await users.CreateAsync(user, input.Password);
        if (!result.Succeeded)
        {
            foreach (var error in result.Errors)
                ModelState.AddModelError("", error.Description);
            return View(input);
        }
        await signIn.SignInAsync(user, false);
        return RedirectToAction("Index", "Library");
    }

    [HttpGet]
    public async Task<IActionResult> Login(string? returnUrl, bool externalError = false)
    {
        await SetGoogleAvailabilityAsync();
        if (externalError)
            ModelState.AddModelError("", "Google sign-in was not completed. Please try again.");
        return View(new LoginInput { ReturnUrl = returnUrl });
    }

    [HttpPost]
    public async Task<IActionResult> Login(LoginInput input)
    {
        await SetGoogleAvailabilityAsync();
        if (!ModelState.IsValid)
            return View(input);
        var result = await signIn.PasswordSignInAsync(input.Email.Trim(), input.Password, false, true);
        if (!result.Succeeded)
        {
            ModelState.AddModelError("", "Sign-in failed. Check your email and password.");
            return View(input);
        }
        return !string.IsNullOrEmpty(input.ReturnUrl) && Url.IsLocalUrl(input.ReturnUrl)
            ? LocalRedirect(input.ReturnUrl) : RedirectToAction("Index", "Library");
    }

    [HttpPost]
    public async Task<IActionResult> Logout()
    {
        await signIn.SignOutAsync();
        return RedirectToAction("Index", "Games");
    }

    [HttpPost]
    public async Task<IActionResult> GoogleLogin(string? returnUrl)
    {
        if (!await GoogleAvailableAsync())
            return GoogleFailure("Google sign-in is currently unavailable.");
        await HttpContext.SignOutAsync(IdentityConstants.ExternalScheme);
        var callback = Url.Action(nameof(GoogleCallback), new { returnUrl = LocalReturnUrl(returnUrl) });
        return Challenge(signIn.ConfigureExternalAuthenticationProperties("Google", callback!), "Google");
    }

    [HttpGet]
    public async Task<IActionResult> GoogleCallback(string? returnUrl)
    {
        var info = await signIn.GetExternalLoginInfoAsync();
        await HttpContext.SignOutAsync(IdentityConstants.ExternalScheme);
        if (info is null || info.LoginProvider != "Google" || !GoogleEmailVerified(info))
            return GoogleFailure("Google could not verify your account. Please try again.");

        var result = await signIn.ExternalLoginSignInAsync("Google", info.ProviderKey, false);
        if (result.Succeeded)
            return LocalRedirect(LocalReturnUrl(returnUrl));
        if (result.IsLockedOut || result.IsNotAllowed || result.RequiresTwoFactor)
            return GoogleFailure("This account cannot sign in right now.");

        var email = info.Principal.FindFirstValue(ClaimTypes.Email);
        if (string.IsNullOrWhiteSpace(email))
            return GoogleFailure("Google did not provide an email address.");
        if (await users.FindByEmailAsync(email) is not null)
            return GoogleFailure("An account with this email already exists. Sign in with your password, then connect Google in Account settings.");

        var user = new IdentityUser { UserName = email, Email = email, EmailConfirmed = true };
        var created = await users.CreateAsync(user);
        if (!created.Succeeded)
            return GoogleFailure("Your account could not be created. Please try signing in again.");
        var linked = await users.AddLoginAsync(user, info);
        if (!linked.Succeeded)
        {
            await users.DeleteAsync(user);
            return GoogleFailure("Google could not be connected. Please try again.");
        }
        await signIn.SignInAsync(user, false, "Google");
        return LocalRedirect(LocalReturnUrl(returnUrl));
    }

    [Authorize, HttpGet]
    public async Task<IActionResult> Settings()
    {
        var user = await users.GetUserAsync(User);
        if (user is null)
            return Challenge();
        var logins = await users.GetLoginsAsync(user);
        return View(new AccountSettingsPage(user.Email ?? "", await users.HasPasswordAsync(user),
            logins.Any(login => login.LoginProvider == "Google"), await GoogleAvailableAsync(), await plans.GetAsync(user.Id)));
    }

    [Authorize, HttpPost]
    public async Task<IActionResult> ChangePlan(AccountPlan plan)
    {
        if (!plans.CanChangePlan)
            return NotFound();
        if (!ModelState.IsValid || !Enum.IsDefined(plan))
            return BadRequest();
        await plans.SetAsync(User.FindFirstValue(ClaimTypes.NameIdentifier)!, plan);
        TempData["Message"] = $"Your demo plan is now {plan}.";
        return RedirectToAction(nameof(Settings));
    }

    [Authorize, HttpPost]
    public async Task<IActionResult> LinkGoogle()
    {
        if (!await GoogleAvailableAsync())
        {
            TempData["Message"] = "Google sign-in is currently unavailable.";
            return RedirectToAction(nameof(Settings));
        }
        var user = await users.GetUserAsync(User);
        if (user is null)
            return Challenge();
        await HttpContext.SignOutAsync(IdentityConstants.ExternalScheme);
        var callback = Url.Action(nameof(LinkGoogleCallback));
        return Challenge(signIn.ConfigureExternalAuthenticationProperties("Google", callback!, user.Id), "Google");
    }

    [Authorize, HttpGet]
    public async Task<IActionResult> LinkGoogleCallback()
    {
        var user = await users.GetUserAsync(User);
        if (user is null)
            return Challenge();
        var info = await signIn.GetExternalLoginInfoAsync(user.Id);
        await HttpContext.SignOutAsync(IdentityConstants.ExternalScheme);
        if (info is null || info.LoginProvider != "Google" || !GoogleEmailVerified(info))
        {
            TempData["Message"] = "Google could not be connected. Please try again from Account settings.";
            return RedirectToAction(nameof(Settings));
        }
        var result = await users.AddLoginAsync(user, info);
        TempData["Message"] = result.Succeeded ? "Google is now connected to your account." : "This Google account could not be connected.";
        return RedirectToAction(nameof(Settings));
    }

    private async Task<bool> GoogleAvailableAsync() =>
        (await signIn.GetExternalAuthenticationSchemesAsync()).Any(scheme => scheme.Name == "Google");

    private async Task SetGoogleAvailabilityAsync() => ViewData["GoogleAvailable"] = await GoogleAvailableAsync();

    private string LocalReturnUrl(string? returnUrl) =>
        !string.IsNullOrEmpty(returnUrl) && Url.IsLocalUrl(returnUrl) ? returnUrl : Url.Action("Index", "Library")!;

    private IActionResult GoogleFailure(string message)
    {
        TempData["Message"] = message;
        return RedirectToAction(nameof(Login));
    }

    private static bool GoogleEmailVerified(ExternalLoginInfo info) =>
        bool.TryParse(info.Principal.FindFirstValue("urn:google:email_verified"), out var verified) && verified;
}
