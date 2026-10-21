# SME Invoicing Hub

VAT-compliant invoicing API for South African small businesses. It calculates 15% VAT per line, checks invoices against SARS tax-invoice rules before they are issued, numbers them sequentially, renders a printable tax invoice, creates signed **PayFast** payment links, marks invoices paid from PayFast's ITN webhook and sends overdue reminders on a schedule.

Built with ASP.NET Core 8 minimal APIs, EF Core and SQLite. Runs in PayFast **sandbox** mode by default, so you can try everything without real credentials.

## Features

- **VAT engine**: standard-rated (15%), zero-rated and exempt lines, cent-accurate rounding, VAT fraction (15/115) helper
- **SARS rules check** (VAT Act s20): full vs abridged tax invoice thresholds (R50 / R5,000), supplier VAT number, recipient name and address on full tax invoices, VAT number format
- **Sequential numbering** per year (`INV-2026-0001`), assigned only when an invoice is issued
- **Printable tax invoice** at `/api/invoices/{id}/html` (print or "Save as PDF" from the browser)
- **PayFast integration**: signed payment links and an ITN endpoint that verifies the MD5 signature, checks the merchant id and the amount, and (outside sandbox) confirms with PayFast's validate endpoint
- **Payment reminders**: a background service sends reminders 1, 7 and 14 days after the due date (configurable). The default sender logs; plug in email or WhatsApp by implementing `IReminderSender`
- Swagger UI, health check, Dockerfile and docker-compose

## Architecture

```text
src/InvoicingHub.Core   Pure domain logic, no I/O: VatCalculator, SarsInvoiceRules,
                        InvoiceNumbers, ReminderPolicy, PayFastSigner
src/InvoicingHub.Api    ASP.NET Core minimal API: EF Core (SQLite), endpoints,
                        PayFast ITN webhook, ReminderBackgroundService, HTML invoice
tests/InvoicingHub.Tests  xUnit unit tests (Core) + integration tests (WebApplicationFactory)
```

Invoice lifecycle: `Draft → Issued → Paid` (or `Cancelled`). Totals are calculated on the server when the draft is created; numbering and the SARS check happen when it is issued.

## Run it

Requires the [.NET 8 SDK](https://dotnet.microsoft.com/download).

```bash
dotnet run --project src/InvoicingHub.Api
# http://localhost:8080/swagger (same port as Docker; override with --urls)
```

Or with Docker:

```bash
docker compose up --build
# http://localhost:8080/swagger
```

### Try it

```bash
B=http://localhost:8080
curl -X POST $B/api/customers -H 'content-type: application/json' \
  -d '{"name":"Acme Bakery","email":"accounts@acme.test","address":"5 Long St, Cape Town"}'
curl -X POST $B/api/invoices -H 'content-type: application/json' \
  -d '{"customerId":1,"dueDate":"2026-11-30","lines":[{"description":"Website build","quantity":1,"unitPriceExclVat":8000},{"description":"Brown bread","quantity":2,"unitPriceExclVat":20,"treatment":"ZeroRated"}]}'
curl -X POST $B/api/invoices/1/issue
curl $B/api/invoices/1/payment-link   # PayFast sandbox checkout URL
curl $B/api/invoices/1/html > invoice.html   # printable tax invoice (open in a browser)
```

## Tests

```bash
dotnet test
```

The tests cover the VAT maths, SARS thresholds and checks, numbering, the reminder schedule, PayFast encoding and signatures, and the API end to end (issuing, validation errors, HTML output, ITN payment, reminders).

## Configuration

Settings live in `src/InvoicingHub.Api/appsettings.json` and can be overridden with environment variables (`Section__Key`).

| Setting | Default | Purpose |
|---|---|---|
| `ConnectionStrings:Invoicing` | `Data Source=invoicing.db` | SQLite database |
| `Supplier:Name`, `Supplier:Address`, `Supplier:VatNumber` | demo values | Your business details printed on invoices |
| `Reminders:Enabled` | `true` | Run the reminder background service |
| `Reminders:IntervalMinutes` | `60` | How often to check for overdue invoices |
| `Reminders:ScheduleDays` | `[1, 7, 14]` | Days after the due date to send reminders |
| `PayFast:Sandbox` | `true` | Use `sandbox.payfast.co.za` and skip server-to-server validation |
| `PayFast:MerchantId`, `PayFast:MerchantKey` | PayFast's public sandbox test merchant | Your merchant credentials (set via env or secrets) |
| `PayFast:Passphrase` | empty (sandbox uses PayFast's sandbox passphrase) | Passphrase set on your PayFast account. **Required in live mode**. In sandbox mode an empty value falls back to PayFast's documented sandbox passphrase, so ITNs are always signature-checked with a passphrase |
| `PayFast:ReturnUrl`, `CancelUrl`, `NotifyUrl` | `http://localhost:8080/...` | Where PayFast sends the buyer and the ITN |

The default PayFast merchant ID and key are PayFast's published sandbox test values, not secrets. Never commit live credentials.

With `PayFast:Sandbox=false` the API **refuses to start** unless `PayFast:Passphrase` and your real `PayFast:MerchantId` and `PayFast:MerchantKey` are set (not the sandbox `10000100` / `46f0cd694581a`). It logs a `crit` line listing every problem and exits with code 1. Without a passphrase, anyone who knows your merchant id could forge an ITN signature. Sandbox and dev still start with an empty passphrase and use PayFast's sandbox passphrase instead.

## Roadmap

- Credit notes for paid invoices
- Xero / Sage Business Cloud sync
- Email and WhatsApp reminder senders

## License

[MIT](LICENSE) © 2026 Ohula Mulaudzi
