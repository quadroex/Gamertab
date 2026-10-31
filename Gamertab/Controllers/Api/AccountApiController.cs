using Gamertab.Models;
using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;

namespace Gamertab.Controllers.Api;

[ApiController, Route("api/account")]
public class AccountApiController(UserManager<IdentityUser> users, SignInManager<IdentityUser> signIn, IAntiforgery antiforgery) : ControllerBase
{
    [HttpGet("session")]
    public IActionResult Session()
    {
        Response.Headers.CacheControl = "no-store";
        return Ok(new { authenticated = User.Identity?.IsAuthenticated == true, email = User.Identity?.Name, csrfToken = antiforgery.GetAndStoreTokens(HttpContext).RequestToken });
    }

    [HttpPost("register")]
    public async Task<IActionResult> Register(RegisterInput input)
    {
        var user = new IdentityUser { UserName = input.Email.Trim(), Email = input.Email.Trim() };
        var result = await users.CreateAsync(user, input.Password);
        if (!result.Succeeded)
            return BadRequest(new { errors = result.Errors.Select(error => error.Description) });
        await signIn.SignInAsync(user, false);
        return StatusCode(201, new { email = user.Email });
    }

    [HttpPost("login")]
    public async Task<IActionResult> Login(LoginInput input)
    {
        var result = await signIn.PasswordSignInAsync(input.Email.Trim(), input.Password, false, true);
        return result.Succeeded ? Ok(new { email = input.Email }) : Unauthorized();
    }

    [Authorize, HttpPost("logout")]
    public async Task<IActionResult> Logout()
    {
        await signIn.SignOutAsync();
        return NoContent();
    }
}
