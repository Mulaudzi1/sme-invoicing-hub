using System.Text.Json.Serialization;
using InvoicingHub.Api;
using InvoicingHub.Core;
using Microsoft.EntityFrameworkCore;

var builder = WebApplication.CreateBuilder(args);

builder.Services.Configure<SupplierSettings>(builder.Configuration.GetSection("Supplier"));
builder.Services.Configure<ReminderSettings>(builder.Configuration.GetSection("Reminders"));
builder.Services.Configure<PayFastOptions>(builder.Configuration.GetSection("PayFast"));

// Fail closed: refuse to start in PayFast live mode without a passphrase or real merchant credentials.
var payFastProblems = (builder.Configuration.GetSection("PayFast").Get<PayFastOptions>() ?? new PayFastOptions()).LiveConfigProblems();
if (payFastProblems.Count > 0)
{
    // Log a clear message and exit with code 1, rather than crashing with an unhandled exception.
    using (var startupLogs = LoggerFactory.Create(b => b.AddSimpleConsole(o => o.SingleLine = true)))
    {
        var log = startupLogs.CreateLogger("InvoicingHub.Startup");
        log.LogCritical("Unsafe PayFast live configuration, refusing to start: {Problems}", string.Join(" ", payFastProblems));
    }
    Environment.ExitCode = 1;
    return;
}
builder.Services.AddDbContext<InvoicingDb>(o =>
    o.UseSqlite(builder.Configuration.GetConnectionString("Invoicing") ?? "Data Source=invoicing.db"));
builder.Services.AddSingleton(TimeProvider.System);
builder.Services.AddScoped<IReminderSender, LoggingReminderSender>();
builder.Services.AddScoped<ReminderRunner>();
builder.Services.AddHostedService<ReminderBackgroundService>();
builder.Services.AddHttpClient("payfast");
builder.Services.ConfigureHttpJsonOptions(o => o.SerializerOptions.Converters.Add(new JsonStringEnumConverter()));
builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen();

var app = builder.Build();

using (var scope = app.Services.CreateScope())
    scope.ServiceProvider.GetRequiredService<InvoicingDb>().Database.EnsureCreated();

app.UseSwagger();
app.UseSwaggerUI();
app.MapGet("/", () => Results.Redirect("/swagger"));
app.MapGet("/health", () => Results.Ok(new { status = "ok" }));
app.MapInvoicingEndpoints();
app.MapPayFastEndpoints();

app.Run();

public partial class Program;
