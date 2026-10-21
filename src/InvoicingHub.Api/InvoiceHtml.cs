using System.Globalization;
using System.Net;
using System.Text;
using InvoicingHub.Core;

namespace InvoicingHub.Api;

/// <summary>Renders a printable tax invoice (use the browser's "Save as PDF").</summary>
public static class InvoiceHtml
{
    private static readonly CultureInfo Za = CultureInfo.GetCultureInfo("en-ZA");
    private static string R(decimal v) => "R " + v.ToString("N2", Za);
    private static string E(string? s) => WebUtility.HtmlEncode(s ?? "");

    public static string Render(Invoice invoice, SupplierSettings supplier)
    {
        var c = invoice.Customer!;
        var kind = SarsInvoiceRules.KindFor(invoice.Total);
        var sb = new StringBuilder();
        sb.Append($$"""
<!doctype html><html lang="en"><head><meta charset="utf-8"><title>{{E(invoice.Number)}}</title>
<style>body{font-family:system-ui,sans-serif;max-width:800px;margin:2rem auto;color:#222}
table{width:100%;border-collapse:collapse}td,th{padding:.4rem;border-bottom:1px solid #ddd;text-align:left}
.r{text-align:right}h1{margin-bottom:0}.muted{color:#666}</style></head><body>
<h1>{{(kind == TaxInvoiceKind.Full ? "Tax Invoice" : "Tax Invoice (abridged)")}}</h1>
<p class="muted">{{E(invoice.Number)}} · Issued {{invoice.IssueDate:yyyy-MM-dd}} · Due {{invoice.DueDate:yyyy-MM-dd}}</p>
<table><tr><td><strong>{{E(supplier.Name)}}</strong><br>{{E(supplier.Address)}}<br>VAT No. {{E(supplier.VatNumber)}}</td>
<td><strong>Bill to:</strong> {{E(c.Name)}}<br>{{E(c.Address)}}{{(string.IsNullOrEmpty(c.VatNumber) ? "" : "<br>VAT No. " + E(c.VatNumber))}}</td></tr></table>
<table><thead><tr><th>Description</th><th class="r">Qty</th><th class="r">Unit (excl.)</th><th class="r">VAT</th><th class="r">Amount (excl.)</th></tr></thead><tbody>
""");
        foreach (var l in invoice.Lines)
            sb.Append($"<tr><td>{E(l.Description)}</td><td class=\"r\">{l.Quantity:0.##}</td><td class=\"r\">{R(l.UnitPriceExclVat)}</td><td class=\"r\">{R(l.LineVat)}</td><td class=\"r\">{R(l.Net)}</td></tr>");
        sb.Append($"""
</tbody></table>
<table><tr><td class="r">Subtotal (excl. VAT)</td><td class="r">{R(invoice.Subtotal)}</td></tr>
<tr><td class="r">VAT @ 15%</td><td class="r">{R(invoice.Vat)}</td></tr>
<tr><td class="r"><strong>Total</strong></td><td class="r"><strong>{R(invoice.Total)}</strong></td></tr></table>
</body></html>
""");
        return sb.ToString();
    }
}
