using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Identity;
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

builder.Services.AddSingleton(TimeProvider.System);
builder.Services.AddSingleton<IPasswordHasher<HrAccount>, PasswordHasher<HrAccount>>();
builder.Services.AddScoped<CheckInService>();
builder.Services.AddScoped<HrAuthService>();
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
    });

builder.Services.AddAuthorization();
builder.Services.AddCascadingAuthenticationState();

var app = builder.Build();

if (!app.Environment.IsDevelopment())
{
    app.UseExceptionHandler("/Error", createScopeForErrors: true);
    app.UseHsts();
}

app.UseHttpsRedirection();
app.MapStaticAssets();
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

// GET assumé : le bandeau est rendu en interactif, où <AntiforgeryToken /> n'a pas de HttpContext.
// Le seul risque est une déconnexion forcée par CSRF — sans perte de données.
app.MapGet("/admin/logout", async (HttpContext http) =>
{
    await http.SignOutAsync(HrAuth.Scheme);
    return Results.Redirect("/admin/login");
});

await app.RunAsync();