namespace InvoicingHub.Core;

/// <summary>Decides when an overdue invoice should get its next payment reminder.</summary>
public sealed class ReminderPolicy
{
    /// <summary>Days after the due date on which reminders go out.</summary>
    public IReadOnlyList<int> ScheduleDays { get; }

    public ReminderPolicy(params int[] scheduleDays)
    {
        ScheduleDays = scheduleDays.Length == 0 ? new[] { 1, 7, 14 } : scheduleDays.Distinct().OrderBy(d => d).ToArray();
    }

    public bool IsReminderDue(DateOnly dueDate, int remindersSent, DateOnly today)
    {
        if (remindersSent >= ScheduleDays.Count) return false;
        return today >= dueDate.AddDays(ScheduleDays[remindersSent]);
    }
}
