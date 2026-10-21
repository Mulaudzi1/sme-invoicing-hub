using InvoicingHub.Core;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace InvoicingHub.Api;

public interface IReminderSender
{
    Task SendAsync(Invoice invoice, Customer customer, int sequence, CancellationToken ct);
}

/// <summary>Default sender: writes the reminder to the log. Swap in SMTP, SendGrid or WhatsApp in production.</summary>
public sealed class LoggingReminderSender(ILogger<LoggingReminderSender> logger) : IReminderSender
{
    public Task SendAsync(Invoice invoice, Customer customer, int sequence, CancellationToken ct)
    {
        logger.LogInformation("Reminder {Sequence} for {Number} (R{Total}) to {Email}",
            sequence, invoice.Number, invoice.Total, customer.Email ?? customer.Name);
        return Task.CompletedTask;
    }
}

public sealed class ReminderRunner(InvoicingDb db, IReminderSender sender, IOptions<ReminderSettings> settings, TimeProvider clock)
{
    public async Task<int> RunOnceAsync(CancellationToken ct = default)
    {
        var policy = new ReminderPolicy(settings.Value.ScheduleDays);
        var now = clock.GetUtcNow();
        var today = DateOnly.FromDateTime(now.UtcDateTime);
        var candidates = await db.Invoices.Include(i => i.Customer)
            .Where(i => i.Status == InvoiceStatus.Issued && i.DueDate < today)
            .ToListAsync(ct);

        var sent = 0;
        foreach (var invoice in candidates.Where(i => policy.IsReminderDue(i.DueDate, i.RemindersSent, today)))
        {
            var sequence = invoice.RemindersSent + 1;
            await sender.SendAsync(invoice, invoice.Customer!, sequence, ct);
            invoice.RemindersSent = sequence;
            db.Reminders.Add(new ReminderLog
            {
                InvoiceId = invoice.Id, Sequence = sequence, SentAt = now,
                Recipient = invoice.Customer!.Email ?? invoice.Customer.Name,
            });
            sent++;
        }
        await db.SaveChangesAsync(ct);
        return sent;
    }
}

public sealed class ReminderBackgroundService(IServiceScopeFactory scopes, IOptions<ReminderSettings> settings, ILogger<ReminderBackgroundService> logger)
    : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!settings.Value.Enabled) return;
        using var timer = new PeriodicTimer(TimeSpan.FromMinutes(Math.Max(1, settings.Value.IntervalMinutes)));
        do
        {
            try
            {
                using var scope = scopes.CreateScope();
                var sent = await scope.ServiceProvider.GetRequiredService<ReminderRunner>().RunOnceAsync(stoppingToken);
                if (sent > 0) logger.LogInformation("Sent {Count} payment reminders", sent);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogError(ex, "Reminder run failed");
            }
        } while (await timer.WaitForNextTickAsync(stoppingToken));
    }
}
