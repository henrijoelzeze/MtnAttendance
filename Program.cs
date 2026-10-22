using System.Net;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Antiforgery;
using System.Text.RegularExpressions;
using System.Threading.RateLimiting;
using Microsoft.EntityFrameworkCore;
using Microsoft.FluentUI.AspNetCore.Components;
using MtnAttendance.Components;
using MtnAttendance.Data;
using MtnAttendance.Domain;
using MtnAttendance.Services;
using Radzen;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddRazorComponents()
    .AddInteractiveServerComponents();

// Fluent pour le shell (layout, nav, dialogs) ; Radzen pour DataGrid + Chart du tableau de bord.
builder.Services.AddFluentUIComponents();
builder.Services.AddRadzenComponents();

builder.Services.AddDbContextFactory<AppDbContext>(options =>
    options.UseNpgsql(
        builder.Configuration.GetConnectionString("Default")));

// Fail early if a deployment forgets its external PostgreSQL connection.
if (string.IsNullOrWhiteSpace(builder.Configuration.GetConnectionString("Default")) &&
    !builder.Environment.IsDevelopment())
    throw new InvalidOperationException("ConnectionStrings__Default must be configured securely.");

// Komodo/Docker: keep the cookie key ring across container restarts.
var keysPath = builder.Configuration["DataProtection:KeysPath"];
if (!string.IsNullOrWhiteSpace(keysPath))
    builder.Services.AddDataProtection()
        .SetApplicationName("MtnAttendance")
        .PersistKeysToFileSystem(new DirectoryInfo(keysPath));

// Never trust arbitrary internet-supplied X-Forwarded-* headers.
builder.Services.Configure<ForwardedHeadersOptions>(options =>
{
    options.ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto;
    foreach (var raw in (builder.Configuration["ReverseProxy:KnownProxies"] ?? "")
        .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
    {
        if (!IPAddress.TryParse(raw, out var address))
            throw new InvalidOperationException("ReverseProxy__KnownProxies must contain IP addresses.");
        options.KnownProxies.Add(address);
    }
});

builder.Services.AddSingleton(TimeProvider.System);
builder.Services.AddMemoryCache();

builder.Services.AddScoped<CheckInService>();
builder.Services.AddScoped<HrAuthService>();
builder.Services.AddScoped<IHrDirectoryAuthenticator, LdapsHrAuthenticator>();
builder.Services.AddOptions<LdapsOptions>()
    .Bind(builder.Configuration.GetSection(LdapsOptions.SectionName))
    .Validate(o => !string.IsNullOrWhiteSpace(o.PrimaryHost) && o.Port is > 0 and <= 65535 &&
        !string.IsNullOrWhiteSpace(o.UserSearchBase) && !string.IsNullOrWhiteSpace(o.BindUsername) &&
        !string.IsNullOrWhiteSpace(o.BindPassword) &&
        Regex.IsMatch(o.EmployeeNumberAttribute, "^[A-Za-z][A-Za-z0-9-]{0,63}$") &&
        Regex.IsMatch(o.HrDepartmentAttribute, "^[A-Za-z][A-Za-z0-9-]{0,63}$") &&
        (!string.IsNullOrWhiteSpace(o.HrGroupDn) || !string.IsNullOrWhiteSpace(o.HrDepartmentValue)),
        "LDAPS configuration is incomplete. Set protected runtime environment variables.")
    .ValidateOnStart();
builder.Services.AddScoped<EmployeeImportService>();
builder.Services.AddScoped<EventService>();

builder.Services.AddAuthentication(HrAuth.Scheme)
    .AddCookie(HrAuth.Scheme, o =>
    {
        o.Cookie.Name = "mtn.attendance.hr";
        o.Cookie.HttpOnly = true;
        o.Cookie.SameSite = SameSiteMode.Lax;
        o.Cookie.SecurePolicy = CookieSecurePolicy.Always;
        o.LoginPath = "/admin/login";
        o.AccessDeniedPath = "/admin/login";
        o.ExpireTimeSpan = TimeSpan.FromHours(8);
        o.SlidingExpiration = true;
        o.Events.OnValidatePrincipal = async context =>
        {
            var auth = context.HttpContext.RequestServices.GetRequiredService<HrAuthService>();
            if (!await auth.IsAllowedAsync(context.Principal.EmployeeId(), context.HttpContext.RequestAborted))
            {
                context.RejectPrincipal();
                await context.HttpContext.SignOutAsync(HrAuth.Scheme);
            }
        };
    });

builder.Services.AddAuthorization(options =>
    options.DefaultPolicy = new Microsoft.AspNetCore.Authorization.AuthorizationPolicyBuilder(HrAuth.Scheme)
        .RequireAuthenticatedUser().RequireRole("HR").Build());

builder.Services.AddRateLimiter(options =>
{
    options.GlobalLimiter = PartitionedRateLimiter.Create<HttpContext, string>(context =>
    {
        if (!HttpMethods.IsPost(context.Request.Method) || context.Request.Path != "/admin/login")
            return RateLimitPartition.GetNoLimiter("other");
        var ip = context.Connection.RemoteIpAddress?.ToString() ?? "unknown";
        return RateLimitPartition.GetFixedWindowLimiter(ip, _ => new FixedWindowRateLimiterOptions
        {
            PermitLimit = 20, Window = TimeSpan.FromMinutes(1), QueueLimit = 0, AutoReplenishment = true
        });
    });
    options.OnRejected = (context, _) =>
    {
        context.HttpContext.Response.StatusCode = StatusCodes.Status429TooManyRequests;
        return ValueTask.CompletedTask;
    };
});
builder.Services.AddCascadingAuthenticationState();

var app = builder.Build();

if (!app.Environment.IsDevelopment())
{
    app.UseExceptionHandler("/Error", createScopeForErrors: true);
    app.UseHsts();
}

app.UseForwardedHeaders();
if (!builder.Configuration.GetValue<bool>("ReverseProxy:TlsTerminated"))
    app.UseHttpsRedirection();
app.MapStaticAssets();
app.MapGet("/healthz", () => Results.Ok(new { status = "alive" })).AllowAnonymous();
app.UseRateLimiter();
app.UseAuthentication();
app.UseAuthorization();
app.UseAntiforgery();

app.MapRazorComponents<App>()
    .AddInteractiveServerRenderMode();

// QR téléchargeable depuis le tableau de bord de l'événement.
app.MapGet("/admin/events/{id:int}/qr.png", async (int id, EventService events, HttpRequest request) =>
{
    var token = await events.GetQrTokenAsync(id);
    if (token is null) return Results.NotFound();

    var url = $"{request.Scheme}://{request.Host}/c/{token}";
    return Results.File(EventService.QrPng(url, 14), "image/png", $"qr-evenement-{id}.png");
}).RequireAuthorization();

// Export des présents (bouton du tableau de bord).
app.MapGet("/admin/events/{id:int}/attendees.csv", async (int id, EventService events) =>
{
    var file = await events.ExportAttendeesCsvAsync(id);
    return file is null ? Results.NotFound() : Results.File(file.Value.Content, "text/csv", file.Value.FileName);
}).RequireAuthorization();

// Logout requires a valid antiforgery token; GET only shows the confirmation page.
app.MapPost("/admin/logout/confirm", async (HttpContext http, IAntiforgery antiforgery) =>
{
    await antiforgery.ValidateRequestAsync(http);
    await http.SignOutAsync(HrAuth.Scheme);
    return Results.LocalRedirect("/admin/login");
}).RequireAuthorization();

await app.RunAsync();