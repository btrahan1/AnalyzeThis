using AnalyzeThis.Components;

var builder = WebApplication.CreateBuilder(args);

// Add services to the container.
builder.Services.AddRazorComponents()
    .AddInteractiveServerComponents();

builder.Services.AddHttpClient();
builder.Services.AddSingleton<AnalyzeThis.Services.TrafficService>();
builder.Services.AddSingleton<AnalyzeThis.Services.SettingsService>();

builder.Services.AddCors(options =>
{
    options.AddPolicy("AllowAll", policy =>
    {
        policy.AllowAnyOrigin()
              .AllowAnyMethod()
              .AllowAnyHeader();
    });
});

var app = builder.Build();

// Configure the HTTP request pipeline.
if (!app.Environment.IsDevelopment())
{
    app.UseExceptionHandler("/Error", createScopeForErrors: true);
    // The default HSTS value is 30 days. You may want to change this for production scenarios, see https://aka.ms/aspnetcore-hsts.
    app.UseHsts();
}

// app.UseHttpsRedirection();

app.UseCors("AllowAll");

// Add the custom proxy middleware to capture AI traffic
app.UseMiddleware<AnalyzeThis.Middleware.ProxyMiddleware>();


app.UseAntiforgery();

app.MapStaticAssets();
app.MapRazorComponents<App>()
    .AddInteractiveServerRenderMode();

// --- STARTUP AUTO-DISCOVERY ---
// "Do the thing" (reconnaissance) immediately on boot
using (var scope = app.Services.CreateScope())
{
    var settings = scope.ServiceProvider.GetRequiredService<AnalyzeThis.Services.SettingsService>();
    try 
    {
         // We fire this and forget or wait (Wait is fine for local tool startup)
         await settings.RefreshLocalModelsAsync();
         Console.WriteLine(">>> Analysis Gateway: Local Reconnaissance Complete.");
    } 
    catch(Exception ex) 
    {
         Console.WriteLine($">>> Analysis Gateway: Reconnaissance Failed! {ex.Message}");
    }
}

app.Run();
