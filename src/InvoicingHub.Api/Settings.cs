namespace InvoicingHub.Api;

public sealed class SupplierSettings
{
    public string Name { get; set; } = "";
    public string Address { get; set; } = "";
    public string VatNumber { get; set; } = "";
}

public sealed class ReminderSettings
{
    public bool Enabled { get; set; } = true;
    public int IntervalMinutes { get; set; } = 60;
    // Empty means the default schedule (1, 7 and 14 days after the due date).
    public int[] ScheduleDays { get; set; } = [];
}
