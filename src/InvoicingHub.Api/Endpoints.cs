using InvoicingHub.Core;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace InvoicingHub.Api;

public sealed record CreateCustomer(string Name, string? Email, string? Address, string? VatNumber);
public sealed record CreateInvoice(int CustomerId, DateOnly DueDate, List<LineInput> Lines);

public static class InvoicingEndpoints
{
    public static void MapInvoicingEndpoints(this IEndpointRouteBuilder app)
    {
        var customers = app.MapGroup("/api/customers").WithTags("Customers");
        customers.MapGet("/", async (InvoicingDb db) => await db.Customers.OrderBy(c => c.Name).ToListAsync());
        customers.MapPost("/", async (CreateCustomer dto, InvoicingDb db) =>
        {
            if (string.IsNullOrWhiteSpace(dto.Name))
                return Results.ValidationProblem(new Dictionary<string, string[]> { ["name"] = ["Name is required."] });
            if (!string.IsNullOrWhiteSpace(dto.VatNumber) && !SarsInvoiceRules.IsValidVatNumber(dto.VatNumber))
                return Results.ValidationProblem(new Dictionary<string, string[]> { ["vatNumber"] = ["VAT number must be 10 digits starting with 4."] });
            var customer = new Customer { Name = dto.Name.Trim(), Email = dto.Email, Address = dto.Address, VatNumber = dto.VatNumber };
            db.Customers.Add(customer);
            await db.SaveChangesAsync();
            return Results.Created($"/api/customers/{customer.Id}", customer);
        });

        var invoices = app.MapGroup("/api/invoices").WithTags("Invoices");
        invoices.MapGet("/", async (InvoicingDb db, InvoiceStatus? status) =>
            await db.Invoices.Include(i => i.Lines)
                .Where(i => status == null || i.Status == status)
                .OrderByDescending(i => i.Id).ToListAsync());

        invoices.MapGet("/{id:int}", async (int id, InvoicingDb db) =>
            await db.Invoices.Include(i => i.Lines).Include(i => i.Customer).FirstOrDefaultAsync(i => i.Id == id)
                is { } invoice ? Results.Ok(invoice) : Results.NotFound());

        invoices.MapPost("/", async (CreateInvoice dto, InvoicingDb db) =>
        {
            if (await db.Customers.FindAsync(dto.CustomerId) is null)
                return Results.ValidationProblem(new Dictionary<string, string[]> { ["customerId"] = ["Customer not found."] });
            InvoiceTotals totals;
            try { totals = VatCalculator.Calculate(dto.Lines ?? []); }
            catch (ArgumentException ex)
            {
                return Results.ValidationProblem(new Dictionary<string, string[]> { ["lines"] = [ex.Message] });
            }

            var invoice = new Invoice
            {
                CustomerId = dto.CustomerId, DueDate = dto.DueDate,
                Subtotal = totals.Subtotal, Vat = totals.Vat, Total = totals.Total,
                Lines = dto.Lines!.Select((l, i) => new InvoiceLine
                {
                    Description = l.Description, Quantity = l.Quantity, UnitPriceExclVat = l.UnitPriceExclVat,
                    Treatment = l.Treatment, Net = totals.Lines[i].Net, LineVat = totals.Lines[i].Vat,
                }).ToList(),
            };
            db.Invoices.Add(invoice);
            await db.SaveChangesAsync();
            return Results.Created($"/api/invoices/{invoice.Id}", invoice);
        });

        invoices.MapPost("/{id:int}/issue", async (int id, InvoicingDb db, IOptions<SupplierSettings> supplier, TimeProvider clock) =>
        {
            var invoice = await db.Invoices.Include(i => i.Lines).Include(i => i.Customer).FirstOrDefaultAsync(i => i.Id == id);
            if (invoice is null) return Results.NotFound();
            if (invoice.Status != InvoiceStatus.Draft)
                return Results.Conflict(new { message = $"Invoice is already {invoice.Status}." });

            var s = supplier.Value;
            var c = invoice.Customer!;
            var errors = SarsInvoiceRules.Validate(new Supplier(s.Name, s.Address, s.VatNumber),
                new Recipient(c.Name, c.Address, c.VatNumber), invoice.Total, invoice.Lines.Count);
            if (errors.Count > 0)
                return Results.ValidationProblem(new Dictionary<string, string[]> { ["sars"] = errors.ToArray() });

            var today = DateOnly.FromDateTime(clock.GetUtcNow().UtcDateTime);
            var seq = await db.Sequences.FindAsync(today.Year);
            if (seq is null) { seq = new InvoiceSequence { Year = today.Year }; db.Sequences.Add(seq); }
            seq.LastNumber++;
            invoice.Number = InvoiceNumbers.Format(today.Year, seq.LastNumber);
            invoice.IssueDate = today;
            invoice.Status = InvoiceStatus.Issued;
            await db.SaveChangesAsync();
            return Results.Ok(invoice);
        });

        invoices.MapPost("/{id:int}/cancel", async (int id, InvoicingDb db) =>
        {
            var invoice = await db.Invoices.FindAsync(id);
            if (invoice is null) return Results.NotFound();
            if (invoice.Status == InvoiceStatus.Paid)
                return Results.Conflict(new { message = "Paid invoices need a credit note, not a cancellation." });
            invoice.Status = InvoiceStatus.Cancelled;
            await db.SaveChangesAsync();
            return Results.Ok(invoice);
        });

        invoices.MapGet("/{id:int}/html", async (int id, InvoicingDb db, IOptions<SupplierSettings> supplier) =>
        {
            var invoice = await db.Invoices.Include(i => i.Lines).Include(i => i.Customer).FirstOrDefaultAsync(i => i.Id == id);
            if (invoice is null) return Results.NotFound();
            if (invoice.Status == InvoiceStatus.Draft) return Results.Conflict(new { message = "Issue the invoice first." });
            return Results.Content(InvoiceHtml.Render(invoice, supplier.Value), "text/html");
        });

        invoices.MapGet("/{id:int}/payment-link", async (int id, InvoicingDb db, IOptions<PayFastOptions> payfast) =>
        {
            var invoice = await db.Invoices.Include(i => i.Customer).FirstOrDefaultAsync(i => i.Id == id);
            if (invoice is null) return Results.NotFound();
            if (invoice.Status != InvoiceStatus.Issued) return Results.Conflict(new { message = "Only issued, unpaid invoices can be paid." });
            var url = PayFastSigner.BuildPaymentUrl(payfast.Value, invoice.Number!, invoice.Total, $"Invoice {invoice.Number}", invoice.Customer?.Email);
            return Results.Ok(new { url, sandbox = payfast.Value.Sandbox });
        });

        app.MapPost("/api/reminders/run", async (ReminderRunner runner) => Results.Ok(new { sent = await runner.RunOnceAsync() }))
            .WithTags("Reminders");
        app.MapGet("/api/reminders", async (InvoicingDb db) => await db.Reminders.OrderByDescending(r => r.Id).ToListAsync())
            .WithTags("Reminders");
    }
}
