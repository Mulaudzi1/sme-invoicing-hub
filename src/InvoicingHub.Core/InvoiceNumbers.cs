namespace InvoicingHub.Core;

public static class InvoiceNumbers
{
    /// <summary>Sequential, gap-free per-year numbering such as INV-2026-0042.</summary>
    public static string Format(int year, int sequence)
    {
        if (sequence < 1) throw new ArgumentOutOfRangeException(nameof(sequence));
        return $"INV-{year}-{sequence:0000}";
    }
}
