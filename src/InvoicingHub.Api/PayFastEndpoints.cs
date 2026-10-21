using System.Globalization;
using InvoicingHub.Core;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace InvoicingHub.Api;

public static class PayFastEndpoints
{
    public static void MapPayFastEndpoints(this IEndpointRouteBuilder app)
    {
        // PayFast Instant Transaction Notification (ITN) webhook.
        app.MapPost("/api/payfast/itn", async (HttpRequest request, InvoicingDb db, IOptions<PayFastOptions> options,
            IHttpClientFactory http, TimeProvider clock, ILogger<PayFastOptions> logger) =>
        {
            using var reader = new StreamReader(request.Body);
            var body = await reader.ReadToEndAsync();
            var fields = ParseForm(body);
            var get = (string key) => fields.FirstOrDefault(f => f.Key == key).Value;

            if (!PayFastSigner.Verify(fields, options.Value.Passphrase, get("signature")))
            {
                logger.LogWarning("Rejected ITN with invalid signature");
                return Results.BadRequest(new { message = "Invalid signature." });
            }

            // Outside sandbox mode, confirm the notification with PayFast server-to-server.
            if (!options.Value.Sandbox)
            {
                var client = http.CreateClient("payfast");
                var validate = await client.PostAsync("https://www.payfast.co.za/eng/query/validate",
                    new StringContent(body, System.Text.Encoding.UTF8, "application/x-www-form-urlencoded"));
                if ((await validate.Content.ReadAsStringAsync()).Trim() != "VALID")
                    return Results.BadRequest(new { message = "PayFast did not validate this notification." });
            }

            var invoice = await db.Invoices.FirstOrDefaultAsync(i => i.Number == get("m_payment_id"));
            if (invoice is null) return Results.NotFound();
            if (get("payment_status") != "COMPLETE") return Results.Ok(new { status = "ignored" });
            if (invoice.Status == InvoiceStatus.Paid) return Results.Ok(new { status = "already-paid" });

            if (!decimal.TryParse(get("amount_gross"), NumberStyles.Number, CultureInfo.InvariantCulture, out var gross)
                || gross != invoice.Total)
                return Results.BadRequest(new { message = "Amount does not match the invoice total." });

            invoice.Status = InvoiceStatus.Paid;
            invoice.PaidAt = clock.GetUtcNow();
            invoice.PaymentReference = get("pf_payment_id");
            await db.SaveChangesAsync();
            return Results.Ok(new { status = "paid" });
        }).WithTags("PayFast");
    }

    /// <summary>Parses a urlencoded body while keeping field order (needed for the signature).</summary>
    public static List<KeyValuePair<string, string>> ParseForm(string body) =>
        body.Split('&', StringSplitOptions.RemoveEmptyEntries)
            .Select(pair => pair.Split('=', 2))
            .Select(kv => new KeyValuePair<string, string>(Uri.UnescapeDataString(kv[0].Replace('+', ' ')),
                kv.Length > 1 ? Uri.UnescapeDataString(kv[1].Replace('+', ' ')) : ""))
            .ToList();
}
