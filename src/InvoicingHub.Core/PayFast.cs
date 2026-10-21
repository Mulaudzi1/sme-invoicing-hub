using System.Security.Cryptography;
using System.Text;

namespace InvoicingHub.Core;

public sealed class PayFastOptions
{
    public bool Sandbox { get; set; } = true;
    public string MerchantId { get; set; } = "";
    public string MerchantKey { get; set; } = "";
    public string? Passphrase { get; set; }
    public string ReturnUrl { get; set; } = "";
    public string CancelUrl { get; set; } = "";
    public string NotifyUrl { get; set; } = "";

    public string ProcessUrl => Sandbox
        ? "https://sandbox.payfast.co.za/eng/process"
        : "https://www.payfast.co.za/eng/process";
}

/// <summary>Builds signed PayFast payment links and verifies ITN signatures.</summary>
public static class PayFastSigner
{
    /// <summary>URL-encodes like PHP's urlencode(): unreserved = A-Z a-z 0-9 - _ . ; space becomes '+'; upper-case hex.</summary>
    public static string Encode(string value)
    {
        var sb = new StringBuilder();
        foreach (var b in Encoding.UTF8.GetBytes(value))
        {
            var c = (char)b;
            if (char.IsAsciiLetterOrDigit(c) || c is '-' or '_' or '.') sb.Append(c);
            else if (c == ' ') sb.Append('+');
            else sb.Append('%').Append(b.ToString("X2"));
        }
        return sb.ToString();
    }

    /// <summary>Checkout signature: concatenates the non-empty fields in order, appends the passphrase and returns the MD5 hex digest.</summary>
    public static string Sign(IEnumerable<KeyValuePair<string, string>> orderedFields, string? passphrase)
    {
        var parts = orderedFields
            .Where(f => !string.IsNullOrEmpty(f.Value) && f.Key != "signature")
            .Select(f => $"{f.Key}={Encode(f.Value.Trim())}")
            .ToList();
        if (!string.IsNullOrEmpty(passphrase)) parts.Add($"passphrase={Encode(passphrase.Trim())}");
        var hash = MD5.HashData(Encoding.UTF8.GetBytes(string.Join("&", parts)));
        return Convert.ToHexString(hash).ToLowerInvariant();
    }

    /// <summary>
    /// ITN signature: every field PayFast posted, in posted order, up to (not including) "signature",
    /// <b>including empty values</b> and without trimming, then the passphrase. This differs from the
    /// checkout signature (<see cref="Sign"/>), which skips empty fields.
    /// </summary>
    public static string SignItn(IEnumerable<KeyValuePair<string, string>> postedFields, string? passphrase)
    {
        var parts = new List<string>();
        foreach (var f in postedFields)
        {
            if (f.Key == "signature") break;
            parts.Add($"{f.Key}={Encode(f.Value ?? "")}");
        }
        if (!string.IsNullOrEmpty(passphrase)) parts.Add($"passphrase={Encode(passphrase.Trim())}");
        var hash = MD5.HashData(Encoding.UTF8.GetBytes(string.Join("&", parts)));
        return Convert.ToHexString(hash).ToLowerInvariant();
    }

    /// <summary>Verifies an ITN signature (see <see cref="SignItn"/>).</summary>
    public static bool Verify(IEnumerable<KeyValuePair<string, string>> orderedFields, string? passphrase, string? signature)
    {
        if (string.IsNullOrEmpty(signature)) return false;
        var expected = Encoding.ASCII.GetBytes(SignItn(orderedFields, passphrase));
        var actual = Encoding.ASCII.GetBytes(signature.ToLowerInvariant());
        return CryptographicOperations.FixedTimeEquals(expected, actual);
    }

    public static string BuildPaymentUrl(PayFastOptions options, string paymentId, decimal amount, string itemName, string? email = null)
    {
        // Field order matters for the signature and follows PayFast's documentation.
        var fields = new List<KeyValuePair<string, string>>
        {
            new("merchant_id", options.MerchantId),
            new("merchant_key", options.MerchantKey),
            new("return_url", options.ReturnUrl),
            new("cancel_url", options.CancelUrl),
            new("notify_url", options.NotifyUrl),
            new("email_address", email ?? ""),
            new("m_payment_id", paymentId),
            new("amount", amount.ToString("0.00", System.Globalization.CultureInfo.InvariantCulture)),
            new("item_name", itemName),
        };
        var signature = Sign(fields, options.Passphrase);
        var query = string.Join("&", fields.Where(f => !string.IsNullOrEmpty(f.Value)).Select(f => $"{f.Key}={Encode(f.Value)}"));
        return $"{options.ProcessUrl}?{query}&signature={signature}";
    }
}
