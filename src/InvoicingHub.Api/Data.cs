using InvoicingHub.Core;
using Microsoft.EntityFrameworkCore;

namespace InvoicingHub.Api;

public enum InvoiceStatus { Draft, Issued, Paid, Cancelled }

public class Customer
{
    public int Id { get; set; }
    public required string Name { get; set; }
    public string? Email { get; set; }
    public string? Address { get; set; }
    public string? VatNumber { get; set; }
}

public class Invoice
{
    public int Id { get; set; }
    public string? Number { get; set; }
    public int CustomerId { get; set; }
    public Customer? Customer { get; set; }
    public InvoiceStatus Status { get; set; } = InvoiceStatus.Draft;
    public DateOnly? IssueDate { get; set; }
    public DateOnly DueDate { get; set; }
    public List<InvoiceLine> Lines { get; set; } = new();
    public decimal Subtotal { get; set; }
    public decimal Vat { get; set; }
    public decimal Total { get; set; }
    public DateTimeOffset? PaidAt { get; set; }
    public string? PaymentReference { get; set; }
    public int RemindersSent { get; set; }
}

public class InvoiceLine
{
    public int Id { get; set; }
    public int InvoiceId { get; set; }
    public required string Description { get; set; }
    public decimal Quantity { get; set; }
    public decimal UnitPriceExclVat { get; set; }
    public VatTreatment Treatment { get; set; }
    public decimal Net { get; set; }
    public decimal LineVat { get; set; }
}

public class ReminderLog
{
    public int Id { get; set; }
    public int InvoiceId { get; set; }
    public int Sequence { get; set; }
    public DateTimeOffset SentAt { get; set; }
    public required string Recipient { get; set; }
}

public class InvoiceSequence
{
    public int Year { get; set; }
    public int LastNumber { get; set; }
}

public class InvoicingDb(DbContextOptions<InvoicingDb> options) : DbContext(options)
{
    public DbSet<Customer> Customers => Set<Customer>();
    public DbSet<Invoice> Invoices => Set<Invoice>();
    public DbSet<InvoiceLine> InvoiceLines => Set<InvoiceLine>();
    public DbSet<ReminderLog> Reminders => Set<ReminderLog>();
    public DbSet<InvoiceSequence> Sequences => Set<InvoiceSequence>();

    protected override void OnModelCreating(ModelBuilder b)
    {
        b.Entity<InvoiceSequence>().HasKey(s => s.Year);
        b.Entity<Invoice>().HasIndex(i => i.Number).IsUnique();
        b.Entity<Invoice>().Property(i => i.Status).HasConversion<string>();
        b.Entity<InvoiceLine>().Property(l => l.Treatment).HasConversion<string>();
        // SQLite has no decimal type; store money as TEXT to keep exact cents.
        foreach (var p in b.Model.GetEntityTypes().SelectMany(t => t.GetProperties()).Where(p => p.ClrType == typeof(decimal)))
            p.SetColumnType("TEXT");
    }
}
