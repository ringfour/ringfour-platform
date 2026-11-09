namespace Ringfour.Api.Tests;

public sealed class StartupGuardTests
{
    [Fact]
    public void Test_signing_key_is_rejected_in_production()
    {
        using var factory = new ApiFactory(
            "Host=unused;Database=unused;Username=unused;Password=unused",
            migrateOnStartup: false,
            productionEnvironment: true);

        var exception = Record.Exception(() => factory.CreateClient());

        Assert.NotNull(exception);
        Assert.Contains("test-only", exception.ToString());
    }

    [Fact]
    public void Missing_auth0_configuration_is_rejected()
    {
        using var factory = new ApiFactory(
            "Host=unused;Database=unused;Username=unused;Password=unused",
            migrateOnStartup: false,
            includeTestAuth: false);

        var exception = Record.Exception(() => factory.CreateClient());

        Assert.NotNull(exception);
        Assert.Contains(
            "Auth0:Domain and Auth0:Audience are required", exception.ToString());
    }
}
