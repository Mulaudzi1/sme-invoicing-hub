using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using InvoicingHub.Api;
using InvoicingHub.Core;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;

namespace InvoicingHub.Tests;

public sealed class FixedClock(DateTimeOffset now) : TimeProvider
{
    public DateTimeOffset Now { get; set; } = now;
    public override DateTimeOffset GetUtcNow() => Now;
}

public sealed class ApiFactory : WebApplicationFactory<Program>
{
    private readonly string _db = Path.Combine(Path.GetTempPath(), $"invoicing-test-{Guid.NewGuid():N}.db");
    public FixedClock Clock { get; } = new(new DateTimeOffset(2026, 10, 1, 8, 0, 0, TimeSpan.Zero));

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseSetting("ConnectionStrings:Invoicing", $"Data Source={_db}");
        builder.UseSetting("Reminders:Enabled", "false");
        builder.UseSetting("PayFast:Passphrase", "test-passphrase");
        builder.ConfigureServices(s => s.AddSingleton<TimeProvider>(Clock));
    }

    protected override void Dispose(bool disposing)
    {
        base.Dispose(disposing);
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        File.Delete(_db);
    }
}

public class ApiTests : IClassFixture<ApiFactory>
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { Converters = { new JsonStringEnumConverter() } };
    private readonly ApiFactory _factory;
    private readonly HttpClient _client;

    public ApiTests(ApiFactory factory)
    {
        _factory = factory;
        _client = factory.CreateClient();
    }

    private async Task<Invoice> CreateIssuedInvoice(decimal unitPrice, string? address = "5 Long St, Cape Town")
    {
        var customer = await (await _client.PostAsJsonAsync("/api/customers",
            new CreateCustomer("Acme Bakery", "accounts@acme.test", address, null))).Content.ReadFromJsonAsync<Customer>(Json);
        var created = await _client.PostAsJsonAsync("/api/invoices",
            new CreateInvoice(customer!.Id, new DateOnly(2026, 10, 15), [new LineInput("Monthly support", 1, unitPrice)]), Json);
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        var draft = await created.Content.ReadFromJsonAsync<Invoice>(Json);
        var issued = await _client.PostAsync($"/api/invoices/{draft!.Id}/issue", null);
        Assert.Equal(HttpStatusCode.OK, issued.StatusCode);
        return (await issued.Content.ReadFromJsonAsync<Invoice>(Json))!;
    }

    [Fact]
    public async Task Issuing_assigns_sequential_numbers_and_totals()
    {
        var a = await CreateIssuedInvoice(1000m);
        var b = await CreateIssuedInvoice(1000m);
        Assert.Matches(@"^INV-2026-\d{4}$", a.Number!);
        Assert.Equal(int.Parse(a.Number![^4..]) + 1, int.Parse(b.Number![^4..]));
        Assert.Equal(1150m, a.Total);
        Assert.Equal(InvoiceStatus.Issued, a.Status);
    }

    [Fact]
    public async Task Full_tax_invoice_without_recipient_address_is_rejected()
    {
        var customer = await (await _client.PostAsJsonAsync("/api/customers",
            new CreateCustomer("No Address CC", null, null, null))).Content.ReadFromJsonAsync<Customer>(Json);
        var draft = await (await _client.PostAsJsonAsync("/api/invoices",
            new CreateInvoice(customer!.Id, new DateOnly(2026, 10, 15), [new LineInput("Big job", 1, 9000m)]), Json))
            .Content.ReadFromJsonAsync<Invoice>(Json);
        var response = await _client.PostAsync($"/api/invoices/{draft!.Id}/issue", null);
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Contains("Recipient address", await response.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task Invalid_lines_return_validation_problem()
    {
        var customer = await (await _client.PostAsJsonAsync("/api/customers",
            new CreateCustomer("X", null, null, null))).Content.ReadFromJsonAsync<Customer>(Json);
        var response = await _client.PostAsJsonAsync("/api/invoices", new CreateInvoice(customer!.Id, new DateOnly(2026, 10, 1), []), Json);
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task Html_invoice_shows_tax_invoice_details()
    {
        var invoice = await CreateIssuedInvoice(200m);
        var html = await _client.GetStringAsync($"/api/invoices/{invoice.Id}/html");
        Assert.Contains("Tax Invoice", html);
        Assert.Contains(invoice.Number!, html);
        Assert.Contains("VAT No. 4000000000", html);
    }

    [Fact]
    public async Task Signed_itn_marks_invoice_paid_and_bad_signature_is_rejected()
    {
        var invoice = await CreateIssuedInvoice(500m);
        var fields = new List<KeyValuePair<string, string>>
        {
            new("m_payment_id", invoice.Number!), new("pf_payment_id", "123456"),
            new("payment_status", "COMPLETE"), new("item_name", $"Invoice {invoice.Number}"), new("amount_gross", "575.00"),
        };
        var signature = PayFastSigner.Sign(fields, "test-passphrase");
        string Body(string sig) => string.Join("&", fields.Select(f => $"{f.Key}={PayFastSigner.Encode(f.Value)}")) + "&signature=" + sig;

        var bad = await _client.PostAsync("/api/payfast/itn", new StringContent(Body("deadbeef"), System.Text.Encoding.UTF8, "application/x-www-form-urlencoded"));
        Assert.Equal(HttpStatusCode.BadRequest, bad.StatusCode);

        var ok = await _client.PostAsync("/api/payfast/itn", new StringContent(Body(signature), System.Text.Encoding.UTF8, "application/x-www-form-urlencoded"));
        Assert.Equal(HttpStatusCode.OK, ok.StatusCode);
        var paid = await _client.GetFromJsonAsync<Invoice>($"/api/invoices/{invoice.Id}", Json);
        Assert.Equal(InvoiceStatus.Paid, paid!.Status);
        Assert.Equal("123456", paid.PaymentReference);
    }

    [Fact]
    public async Task Payment_link_points_to_payfast_sandbox()
    {
        var invoice = await CreateIssuedInvoice(100m);
        var link = await _client.GetFromJsonAsync<JsonElement>($"/api/invoices/{invoice.Id}/payment-link");
        Assert.StartsWith("https://sandbox.payfast.co.za/", link.GetProperty("url").GetString());
    }

    [Fact]
    public async Task Reminder_runner_sends_once_per_schedule_step()
    {
        var invoice = await CreateIssuedInvoice(300m); // due 2026-10-15
        _factory.Clock.Now = new DateTimeOffset(2026, 10, 17, 8, 0, 0, TimeSpan.Zero);
        using var scope = _factory.Services.CreateScope();
        var runner = scope.ServiceProvider.GetRequiredService<ReminderRunner>();
        Assert.True(await runner.RunOnceAsync() >= 1);
        await runner.RunOnceAsync(); // same day: nothing new for this invoice
        var reminders = await _client.GetFromJsonAsync<List<ReminderLog>>("/api/reminders", Json);
        Assert.Single(reminders!, r => r.InvoiceId == invoice.Id);
        _factory.Clock.Now = new DateTimeOffset(2026, 10, 1, 8, 0, 0, TimeSpan.Zero);
    }
}
