using System.Net;
using InvoicingHub.Core;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;

namespace InvoicingHub.Tests;

public class LiveConfigTests
{
    private sealed class ConfiguredFactory(Dictionary<string, string?> settings) : WebApplicationFactory<Program>
    {
        private readonly string _db = Path.Combine(Path.GetTempPath(), $"invoicing-live-{Guid.NewGuid():N}.db");

        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.UseSetting("ConnectionStrings:Invoicing", $"Data Source={_db}");
            builder.UseSetting("Reminders:Enabled", "false");
            foreach (var (key, value) in settings) builder.UseSetting(key, value);
        }

        protected override void Dispose(bool disposing)
        {
            base.Dispose(disposing);
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            if (File.Exists(_db)) File.Delete(_db);
        }
    }

    [Fact]
    public void Live_mode_without_passphrase_refuses_to_start()
    {
        using var factory = new ConfiguredFactory(new()
        {
            ["PayFast:Sandbox"] = "false",
            ["PayFast:MerchantId"] = "12345678",
            ["PayFast:MerchantKey"] = "livekey",
            ["PayFast:Passphrase"] = "",
        });
        var ex = Assert.ThrowsAny<Exception>(() => factory.CreateClient());
        Assert.Contains("PayFast:Passphrase", ex.ToString());
    }

    [Fact]
    public void Live_mode_with_sandbox_merchant_refuses_to_start()
    {
        using var factory = new ConfiguredFactory(new()
        {
            ["PayFast:Sandbox"] = "false",
            ["PayFast:Passphrase"] = "a-real-passphrase",
        });
        var ex = Assert.ThrowsAny<Exception>(() => factory.CreateClient());
        Assert.Contains("PayFast:MerchantId", ex.ToString());
    }

    [Fact]
    public async Task Live_mode_with_full_config_starts()
    {
        using var factory = new ConfiguredFactory(new()
        {
            ["PayFast:Sandbox"] = "false",
            ["PayFast:MerchantId"] = "12345678",
            ["PayFast:MerchantKey"] = "livekey",
            ["PayFast:Passphrase"] = "a-real-passphrase",
        });
        var res = await factory.CreateClient().GetAsync("/health");
        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
    }

    [Fact]
    public async Task Sandbox_without_passphrase_still_starts()
    {
        using var factory = new ConfiguredFactory(new() { ["PayFast:Passphrase"] = "" });
        var res = await factory.CreateClient().GetAsync("/health");
        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
    }

    [Theory]
    [InlineData(true, null, "10000100", "k", 0)]
    [InlineData(false, null, "12345678", "k", 1)]
    [InlineData(false, "  ", "12345678", "k", 1)]
    [InlineData(false, "p", "10000100", "k", 1)]
    [InlineData(false, "p", "12345678", "", 1)]
    [InlineData(false, "", "", "", 3)]
    [InlineData(false, "p", "12345678", "k", 0)]
    public void LiveConfigProblems_flags_each_missing_setting(bool sandbox, string? passphrase, string merchantId, string merchantKey, int expected)
    {
        var options = new PayFastOptions { Sandbox = sandbox, Passphrase = passphrase, MerchantId = merchantId, MerchantKey = merchantKey };
        Assert.Equal(expected, options.LiveConfigProblems().Count);
    }
}
