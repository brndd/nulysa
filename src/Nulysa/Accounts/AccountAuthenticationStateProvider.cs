using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.AspNetCore.Components.Server;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.Options;

namespace Nulysa.Accounts;

public sealed class AccountAuthenticationStateProvider(
    ILoggerFactory loggerFactory, IServiceScopeFactory scopes, IOptions<IdentityOptions> options)
    : RevalidatingServerAuthenticationStateProvider(loggerFactory)
{
    protected override TimeSpan RevalidationInterval => TimeSpan.FromSeconds(30);

    protected override async Task<bool> ValidateAuthenticationStateAsync(AuthenticationState authenticationState, CancellationToken cancellationToken)
    {
        // A circuit outlives HTTP cookie validation; recheck gates and stamps in a fresh scope.
        await using var scope = scopes.CreateAsyncScope();
        var users = scope.ServiceProvider.GetRequiredService<UserManager<User>>();
        var user = await users.GetUserAsync(authenticationState.User);
        return user is not null && user.MayLogIn &&
            authenticationState.User.FindFirst(options.Value.ClaimsIdentity.SecurityStampClaimType)?.Value ==
            await users.GetSecurityStampAsync(user);
    }
}
