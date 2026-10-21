using System.Diagnostics;
using System.Net;
using System.Net.Http.Json;
using InvoicingHub.Api;
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
        Assert.ThrowsAny<Exception>(() => factory.CreateClient());
    }

    [Fact]
    public void Live_mode_with_sandbox_merchant_refuses_to_start()
    {
        using var factory = new ConfiguredFactory(new()
        {
            ["PayFast:Sandbox"] = "false",
            ["PayFast:Passphrase"] = "a-real-passphrase",
        });
        Assert.ThrowsAny<Exception>(() => factory.CreateClient());
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
    [InlineData(false, "p", "12345678", "46f0cd694581a", 1)]
    [InlineData(false, "p", "12345678", " 46f0cd694581a ", 1)]
    [InlineData(false, "", "", "", 3)]
    [InlineData(false, "p", "12345678", "k", 0)]
    public void LiveConfigProblems_flags_each_missing_setting(bool sandbox, string? passphrase, string merchantId, string merchantKey, int expected)
    {
        var options = new PayFastOptions { Sandbox = sandbox, Passphrase = passphrase, MerchantId = merchantId, MerchantKey = merchantKey };
        Assert.Equal(expected, options.LiveConfigProblems().Count);
    }

    // I4: the real process logs a clear message and exits 1 (no unhandled exception / abort 134).
    private static (int ExitCode, string Output) RunApi(Dictionary<string, string> env)
    {
        var dll = typeof(Program).Assembly.Location;
        var psi = new ProcessStartInfo(Environment.GetEnvironmentVariable("DOTNET_HOST_PATH") ?? "dotnet", $"\"{dll}\"")
        {
            WorkingDirectory = Path.GetDirectoryName(dll)!,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        psi.Environment["ASPNETCORE_URLS"] = "http://127.0.0.1:0";
        psi.Environment["ConnectionStrings__Invoicing"] = $"Data Source={Path.Combine(Path.GetTempPath(), $"invoicing-proc-{Guid.NewGuid():N}.db")}";
        psi.Environment["Reminders__Enabled"] = "false";
        foreach (var (k, v) in env) psi.Environment[k] = v;
        using var proc = Process.Start(psi)!;
        var stdout = proc.StandardOutput.ReadToEndAsync();
        var stderr = proc.StandardError.ReadToEndAsync();
        if (!proc.WaitForExit(60_000)) { proc.Kill(true); throw new TimeoutException("API did not exit"); }
        return (proc.ExitCode, stdout.Result + stderr.Result);
    }

    [Theory]
    [InlineData("", "12345678", "livekey", "PayFast:Passphrase")]
    [InlineData("a-real-passphrase", "10000100", "livekey", "PayFast:MerchantId")]
    [InlineData("a-real-passphrase", "12345678", "46f0cd694581a", "PayFast:MerchantKey")]
    public void Unsafe_live_config_exits_with_code_1_and_a_clear_message(string passphrase, string merchantId, string merchantKey, string expected)
    {
        var (code, output) = RunApi(new()
        {
            ["PayFast__Sandbox"] = "false",
            ["PayFast__Passphrase"] = passphrase,
            ["PayFast__MerchantId"] = merchantId,
            ["PayFast__MerchantKey"] = merchantKey,
        });
        Assert.Equal(1, code);
        Assert.Contains("Unsafe PayFast live configuration, refusing to start", output);
        Assert.Contains(expected, output);
        Assert.DoesNotContain("Unhandled exception", output);
        Assert.DoesNotContain("   at ", output);
    }

    // I6: sandbox with an empty passphrase verifies with PayFast's sandbox passphrase, so an ITN
    // signed with no passphrase at all is rejected.
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void Sandbox_empty_passphrase_falls_back_to_sandbox_passphrase(string? passphrase)
    {
        var options = new PayFastOptions { Sandbox = true, Passphrase = passphrase };
        Assert.Equal(PayFastOptions.SandboxPassphrase, options.EffectivePassphrase);
    }

    [Fact]
    public void Configured_and_live_passphrases_are_used_as_is()
    {
        Assert.Equal("mine", new PayFastOptions { Sandbox = true, Passphrase = "mine" }.EffectivePassphrase);
        Assert.Equal("live", new PayFastOptions { Sandbox = false, Passphrase = "live" }.EffectivePassphrase);
    }

    [Fact]
    public async Task Sandbox_without_passphrase_rejects_unsigned_passphrase_itn_and_accepts_sandbox_signed()
    {
        using var factory = new ConfiguredFactory(new() { ["PayFast:Passphrase"] = "" });
        var client = factory.CreateClient();
        var json = new System.Text.Json.JsonSerializerOptions(System.Text.Json.JsonSerializerDefaults.Web) { Converters = { new System.Text.Json.Serialization.JsonStringEnumConverter() } };
        var customer = await (await client.PostAsJsonAsync("/api/customers",
            new CreateCustomer("Acme Bakery", "accounts@acme.test", "5 Long St, Cape Town", null))).Content.ReadFromJsonAsync<Customer>(json);
        var created = await client.PostAsJsonAsync("/api/invoices",
            new CreateInvoice(customer!.Id, DateOnly.FromDateTime(DateTime.Today.AddDays(30)), [new LineInput("Monthly support", 1, 100m)]), json);
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        var draft = await created.Content.ReadFromJsonAsync<Invoice>(json);
        var issued = await client.PostAsync($"/api/invoices/{draft!.Id}/issue", null);
        Assert.Equal(HttpStatusCode.OK, issued.StatusCode);
        var number = (await issued.Content.ReadFromJsonAsync<Invoice>(json))!.Number!;

        var fields = new List<KeyValuePair<string, string>>
        {
            new("m_payment_id", number), new("pf_payment_id", "99"), new("payment_status", "COMPLETE"),
            new("amount_gross", "115.00"), new("merchant_id", PayFastOptions.SandboxMerchantId),
        };
        string Body(string? pass) => string.Join("&", fields.Select(f => $"{f.Key}={PayFastSigner.Encode(f.Value)}"))
            + "&signature=" + PayFastSigner.SignItn(fields, pass);
        var form = (string body) => new StringContent(body, System.Text.Encoding.UTF8, "application/x-www-form-urlencoded");

        var forged = await client.PostAsync("/api/payfast/itn", form(Body(null)));
        Assert.Equal(HttpStatusCode.BadRequest, forged.StatusCode);

        var ok = await client.PostAsync("/api/payfast/itn", form(Body(PayFastOptions.SandboxPassphrase)));
        Assert.Equal(HttpStatusCode.OK, ok.StatusCode);
    }
}
