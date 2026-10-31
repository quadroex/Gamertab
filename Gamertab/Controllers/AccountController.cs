using Gamertab.Models;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;

namespace Gamertab.Controllers;

public class AccountController(UserManager<IdentityUser> users, SignInManager<IdentityUser> signIn) : Controller
{
    [HttpGet]
    public IActionResult Register() => View(new RegisterInput());

    [HttpPost]
    public async Task<IActionResult> Register(RegisterInput input)
    {
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
    public IActionResult Login(string? returnUrl) => View(new LoginInput { ReturnUrl = returnUrl });

    [HttpPost]
    public async Task<IActionResult> Login(LoginInput input)
    {
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
}
