namespace InvoicingHub.Core;

/// <summary>How a line is treated for South African VAT.</summary>
public enum VatTreatment
{
    /// <summary>Standard-rated at the current VAT rate (15%).</summary>
    Standard,
    /// <summary>Zero-rated supplies (e.g. exports, basic foodstuffs): VAT at 0%, still on a tax invoice.</summary>
    ZeroRated,
    /// <summary>Exempt supplies (e.g. some financial services): no VAT.</summary>
    Exempt,
}

public sealed record LineInput(string Description, decimal Quantity, decimal UnitPriceExclVat, VatTreatment Treatment = VatTreatment.Standard);

public sealed record LineAmounts(decimal Net, decimal Vat)
{
    public decimal Gross => Net + Vat;
}

public sealed record InvoiceTotals(decimal Subtotal, decimal Vat, decimal Total, IReadOnlyList<LineAmounts> Lines);

public static class VatCalculator
{
    /// <summary>South African standard VAT rate since 1 April 2018.</summary>
    public const decimal StandardRate = 0.15m;

    public static decimal Round(decimal value) => Math.Round(value, 2, MidpointRounding.AwayFromZero);

    public static LineAmounts CalculateLine(LineInput line, decimal rate = StandardRate)
    {
        if (string.IsNullOrWhiteSpace(line.Description))
            throw new ArgumentException("Line description is required.");
        if (line.Quantity <= 0)
            throw new ArgumentException("Quantity must be greater than zero.");
        if (line.UnitPriceExclVat < 0)
            throw new ArgumentException("Unit price cannot be negative.");

        var net = Round(line.Quantity * line.UnitPriceExclVat);
        var vat = line.Treatment == VatTreatment.Standard ? Round(net * rate) : 0m;
        return new LineAmounts(net, vat);
    }

    public static InvoiceTotals Calculate(IEnumerable<LineInput> lines, decimal rate = StandardRate)
    {
        var amounts = lines.Select(l => CalculateLine(l, rate)).ToList();
        if (amounts.Count == 0)
            throw new ArgumentException("An invoice needs at least one line.");
        var subtotal = amounts.Sum(a => a.Net);
        var vat = amounts.Sum(a => a.Vat);
        return new InvoiceTotals(subtotal, vat, subtotal + vat, amounts);
    }

    /// <summary>Splits a VAT-inclusive amount into its VAT portion (tax fraction 15/115).</summary>
    public static decimal VatFromInclusive(decimal inclusiveAmount, decimal rate = StandardRate) =>
        Round(inclusiveAmount * rate / (1 + rate));
}
