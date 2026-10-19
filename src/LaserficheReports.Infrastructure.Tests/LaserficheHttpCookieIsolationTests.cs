using LaserficheReports.Infrastructure.Extensions;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Xunit;

namespace LaserficheReports.Infrastructure.Tests;

public sealed class LaserficheHttpCookieIsolationTests
{
    [Theory]
    [InlineData("LaserficheRaw")]
    [InlineData("LaserficheAuthenticated")]
    public async Task PooledTransport_DoesNotReplayAnotherClientsServerCookie(string clientName)
    {
        var builder = WebApplication.CreateBuilder();
        builder.Logging.ClearProviders();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        await using var server = builder.Build();
        // The token path also exercises the authenticated client's transport,
        // without needing a repository account or making external API calls.
        server.MapGet("/Token", (HttpContext http) =>
        {
            http.Response.Headers.Append("Set-Cookie", "upstream-session=first-user; Path=/");
            return Results.Text(http.Request.Headers.Cookie.ToString());
        });
        await server.StartAsync();
        try
        {
            var address = server.Services.GetRequiredService<IServer>()
                .Features.Get<IServerAddressesFeature>()!.Addresses.Single();
            var services = new ServiceCollection();
            services.AddLogging();
            services.AddSingleton<Microsoft.Extensions.Hosting.IHostEnvironment>(server.Environment);
            services.AddDataProtection();
            services.AddHttpContextAccessor();
            services.AddLaserficheInfrastructure(new ConfigurationBuilder().AddInMemoryCollection(
                new Dictionary<string, string?>
                {
                    ["Laserfiche:ServerUrl"] = address,
                    ["Laserfiche:RepositoryId"] = "TestRepo",
                    ["Laserfiche:ApiVersion"] = "v2"
                }).Build());
            using var provider = services.BuildServiceProvider();
            var factory = provider.GetRequiredService<IHttpClientFactory>();
            using (var first = factory.CreateClient(clientName))
                Assert.Equal("", await first.GetStringAsync(address + "/Token"));
            using (var second = factory.CreateClient(clientName))
                Assert.Equal("", await second.GetStringAsync(address + "/Token"));
        }
        finally { await server.StopAsync(); }
    }
}
