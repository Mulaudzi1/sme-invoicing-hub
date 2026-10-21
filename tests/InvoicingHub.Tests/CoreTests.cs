using InvoicingHub.Core;

namespace InvoicingHub.Tests;

public class VatCalculatorTests
{
    [Fact]
    public void Standard_line_gets_15_percent_vat_rounded_to_cents()
    {
        var line = VatCalculator.CalculateLine(new LineInput("Consulting", 3, 333.33m));
        Assert.Equal(999.99m, line.Net);
        Assert.Equal(150.00m, line.Vat); // 149.9985 rounds half away from zero
        Assert.Equal(1149.99m, line.Gross);
    }

    [Theory]
    [InlineData(VatTreatment.ZeroRated)]
    [InlineData(VatTreatment.Exempt)]
    public void Zero_rated_and_exempt_lines_carry_no_vat(VatTreatment treatment)
    {
        var line = VatCalculator.CalculateLine(new LineInput("Export", 1, 1000m, treatment));
        Assert.Equal(0m, line.Vat);
    }

    [Fact]
    public void Totals_sum_lines()
    {
        var totals = VatCalculator.Calculate([
            new LineInput("Website build", 1, 10000m),
            new LineInput("Brown bread", 2, 20m, VatTreatment.ZeroRated),
        ]);
        Assert.Equal(10040m, totals.Subtotal);
        Assert.Equal(1500m, totals.Vat);
        Assert.Equal(11540m, totals.Total);
    }

    [Fact]
    public void Vat_fraction_of_inclusive_amount()
    {
        Assert.Equal(15m, VatCalculator.VatFromInclusive(115m));
    }

    [Fact]
    public void Rejects_empty_or_invalid_lines()
    {
        Assert.Throws<ArgumentException>(() => VatCalculator.Calculate([]));
        Assert.Throws<ArgumentException>(() => VatCalculator.CalculateLine(new LineInput("x", 0, 1)));
        Assert.Throws<ArgumentException>(() => VatCalculator.CalculateLine(new LineInput("x", 1, -1)));
    }
}

public class SarsRulesTests
{
    private static readonly Supplier GoodSupplier = new("Demo (Pty) Ltd", "1 Main Rd", "4123456789");

    [Theory]
    [InlineData(50, TaxInvoiceKind.NotRequired)]
    [InlineData(50.01, TaxInvoiceKind.Abridged)]
    [InlineData(5000, TaxInvoiceKind.Abridged)]
    [InlineData(5000.01, TaxInvoiceKind.Full)]
    public void Kind_follows_thresholds(decimal total, TaxInvoiceKind expected) =>
        Assert.Equal(expected, SarsInvoiceRules.KindFor(total));

    [Theory]
    [InlineData("4123456789", true)]
    [InlineData("4123 456 789", true)]
    [InlineData("3123456789", false)]
    [InlineData("412345678", false)]
    [InlineData(null, false)]
    public void Vat_number_format(string? value, bool valid) =>
        Assert.Equal(valid, SarsInvoiceRules.IsValidVatNumber(value));

    [Fact]
    public void Full_invoice_needs_recipient_name_and_address()
    {
        var errors = SarsInvoiceRules.Validate(GoodSupplier, new Recipient("Client", null, null), 6000m, 1);
        Assert.Contains(errors, e => e.Contains("Recipient address"));
    }

    [Fact]
    public void Abridged_invoice_does_not_need_recipient_address()
    {
        Assert.Empty(SarsInvoiceRules.Validate(GoodSupplier, new Recipient("Client", null, null), 4000m, 1));
    }

    [Fact]
    public void Supplier_must_be_vat_registered()
    {
        var errors = SarsInvoiceRules.Validate(GoodSupplier with { VatNumber = "123" }, new Recipient("C", "A", null), 100m, 1);
        Assert.Contains(errors, e => e.Contains("Supplier VAT number"));
    }
}

public class NumberingAndReminderTests
{
    [Fact]
    public void Invoice_number_format() => Assert.Equal("INV-2026-0042", InvoiceNumbers.Format(2026, 42));

    [Fact]
    public void Reminders_follow_schedule()
    {
        var policy = new ReminderPolicy(1, 7, 14);
        var due = new DateOnly(2026, 10, 1);
        Assert.False(policy.IsReminderDue(due, 0, due));
        Assert.True(policy.IsReminderDue(due, 0, due.AddDays(1)));
        Assert.False(policy.IsReminderDue(due, 1, due.AddDays(6)));
        Assert.True(policy.IsReminderDue(due, 1, due.AddDays(7)));
        Assert.False(policy.IsReminderDue(due, 3, due.AddDays(100)));
    }
}

public class PayFastTests
{
    [Theory]
    [InlineData("Invoice INV-2026-0001", "Invoice+INV-2026-0001")]
    [InlineData("a@b.co.za", "a%40b.co.za")]
    [InlineData("http://x/y?z=1", "http%3A%2F%2Fx%2Fy%3Fz%3D1")]
    [InlineData("(~*!)", "%28%7E%2A%21%29")]
    public void Encodes_like_php_urlencode(string input, string expected) =>
        Assert.Equal(expected, PayFastSigner.Encode(input));

    [Fact]
    public void Signature_is_md5_of_ordered_fields_with_passphrase()
    {
        var fields = new List<KeyValuePair<string, string>> { new("merchant_id", "10000100"), new("amount", "100.00"), new("empty", "") };
        // md5("merchant_id=10000100&amount=100.00&passphrase=secret")
        var expected = Convert.ToHexString(System.Security.Cryptography.MD5.HashData(
            "merchant_id=10000100&amount=100.00&passphrase=secret"u8.ToArray())).ToLowerInvariant();
        Assert.Equal(expected, PayFastSigner.Sign(fields, "secret"));
    }

    [Fact]
    public void Itn_signature_includes_blank_fields_and_stops_at_signature()
    {
        // Regression: PayFast signs ITNs over every posted field, blank ones included.
        var posted = new List<KeyValuePair<string, string>>
        {
            new("m_payment_id", "INV-1"), new("name_last", ""), new("amount_gross", "100.00"), new("custom_str1", ""),
        };
        var expected = Convert.ToHexString(System.Security.Cryptography.MD5.HashData(
            "m_payment_id=INV-1&name_last=&amount_gross=100.00&custom_str1=&passphrase=secret"u8.ToArray())).ToLowerInvariant();
        Assert.Equal(expected, PayFastSigner.SignItn(posted, "secret"));

        var withSig = posted.Append(new("signature", expected)).Append(new("after", "ignored")).ToList();
        Assert.True(PayFastSigner.Verify(withSig, "secret", expected));
        Assert.False(PayFastSigner.Verify(withSig, "other", expected));
        // The old behaviour (dropping blanks) must not verify.
        Assert.False(PayFastSigner.Verify(withSig, "secret", PayFastSigner.Sign(posted, "secret")));
    }

    [Fact]
    public void Payment_url_targets_sandbox_and_is_signed()
    {
        var url = PayFastSigner.BuildPaymentUrl(new PayFastOptions { MerchantId = "10000100", MerchantKey = "46f0cd694581a" },
            "INV-2026-0001", 1150m, "Invoice INV-2026-0001");
        Assert.StartsWith("https://sandbox.payfast.co.za/eng/process?merchant_id=10000100", url);
        Assert.Contains("amount=1150.00", url);
        Assert.Matches("signature=[0-9a-f]{32}$", url);
    }
}
