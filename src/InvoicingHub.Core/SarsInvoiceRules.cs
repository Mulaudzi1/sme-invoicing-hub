using System.Text.RegularExpressions;

namespace InvoicingHub.Core;

public enum TaxInvoiceKind
{
    /// <summary>Consideration of R50 or less: SARS does not require a tax invoice.</summary>
    NotRequired,
    /// <summary>Consideration above R50 up to R5,000: an abridged tax invoice is enough.</summary>
    Abridged,
    /// <summary>Consideration above R5,000: a full tax invoice with recipient details is required.</summary>
    Full,
}

public sealed record Supplier(string Name, string Address, string VatNumber);

public sealed record Recipient(string Name, string? Address, string? VatNumber);

/// <summary>
/// Checks the information SARS requires on a tax invoice (VAT Act, section 20(4) and 20(5)).
/// This is a practical checklist, not tax advice.
/// </summary>
public static partial class SarsInvoiceRules
{
    public const decimal AbridgedThreshold = 50m;
    public const decimal FullThreshold = 5000m;

    public static TaxInvoiceKind KindFor(decimal totalInclVat) => totalInclVat switch
    {
        <= AbridgedThreshold => TaxInvoiceKind.NotRequired,
        <= FullThreshold => TaxInvoiceKind.Abridged,
        _ => TaxInvoiceKind.Full,
    };

    /// <summary>SA VAT numbers are 10 digits and start with 4.</summary>
    public static bool IsValidVatNumber(string? value) =>
        value is not null && VatNumberPattern().IsMatch(value.Replace(" ", ""));

    public static IReadOnlyList<string> Validate(Supplier supplier, Recipient recipient, decimal totalInclVat, int lineCount)
    {
        var errors = new List<string>();
        if (string.IsNullOrWhiteSpace(supplier.Name)) errors.Add("Supplier name is required.");
        if (string.IsNullOrWhiteSpace(supplier.Address)) errors.Add("Supplier address is required.");
        if (!IsValidVatNumber(supplier.VatNumber))
            errors.Add("Supplier VAT number must be 10 digits starting with 4.");
        if (lineCount == 0) errors.Add("At least one line item is required.");

        if (KindFor(totalInclVat) == TaxInvoiceKind.Full)
        {
            if (string.IsNullOrWhiteSpace(recipient.Name))
                errors.Add("Recipient name is required on a full tax invoice (over R5,000).");
            if (string.IsNullOrWhiteSpace(recipient.Address))
                errors.Add("Recipient address is required on a full tax invoice (over R5,000).");
        }

        if (!string.IsNullOrWhiteSpace(recipient.VatNumber) && !IsValidVatNumber(recipient.VatNumber))
            errors.Add("Recipient VAT number must be 10 digits starting with 4.");

        return errors;
    }

    [GeneratedRegex("^4\\d{9}$")]
    private static partial Regex VatNumberPattern();
}
