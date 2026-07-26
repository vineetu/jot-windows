using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Jot.Tests;

/// <summary>
/// The composition root, validated without instantiating anything.
///
/// <c>ValidateOnBuild</c> walks every constructor-registered service and proves each parameter has a
/// registration. Nothing else in this repo covers that: a service given a new constructor dependency
/// that nobody registered builds cleanly, passes every test, and then throws on launch.
/// (Factory-registered services are opaque to the validator by construction — those are covered by
/// their own tests.)
/// </summary>
public class CompositionRootTests
{
    [Fact]
    public void EveryConstructorInjectedServiceHasItsDependenciesRegistered()
    {
        using ServiceProvider provider = Jot.App.Registrations()
            .BuildServiceProvider(new ServiceProviderOptions { ValidateOnBuild = true, ValidateScopes = true });
        Assert.NotNull(provider);
    }
}
