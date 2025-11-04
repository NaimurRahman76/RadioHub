using Microsoft.EntityFrameworkCore;
using RadioStation.Data;
using RadioStation.Repository;
using RadioStation.Services;
using RadioStation.Hubs;
using System.Text;

var builder = WebApplication.CreateBuilder(args);

// Ensure legacy encodings (e.g., Windows-1252) are available
Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);

// Add services to the container.
builder.Services.AddControllersWithViews();
builder.Services.AddDbContext<ApplicationDbContext>(options => options.UseSqlServer(
	builder.Configuration.GetConnectionString("DefaultConnection")));
builder.Services.AddScoped<ISignUpRepository, SignUpRepository>();
builder.Services.AddDistributedMemoryCache();
builder.Services.AddSession(options =>
{
    options.Cookie.Name = "UserIdSession"; // Set a unique name for your session cookie
    options.IdleTimeout = TimeSpan.FromMinutes(30); // Set the session timeout duration
});

builder.Services.AddSignalR();

// Register the services
builder.Services.AddScoped<ISongService, SongService>();

// Use LiquidSoap streaming service for continuous playback
builder.Services.AddSingleton<IStreamingService, LiquidSoapStreamingService>();
var app = builder.Build();

// Configure the HTTP request pipeline.
if (!app.Environment.IsDevelopment())
{
	app.UseExceptionHandler("/Home/Error");
	// The default HSTS value is 30 days. You may want to change this for production scenarios, see https://aka.ms/aspnetcore-hsts.
	app.UseHsts();
}

app.UseHttpsRedirection();
app.UseStaticFiles();

app.UseRouting();

app.UseAuthorization();
app.UseSession();

// SEO routes
app.MapControllerRoute(
    name: "robots",
    pattern: "robots.txt",
    defaults: new { controller = "Home", action = "RobotsTxt" });

app.MapControllerRoute(
    name: "sitemap",
    pattern: "sitemap.xml",
    defaults: new { controller = "Home", action = "SitemapXml" });


app.MapHub<RadioHub>("/radiohub");

// Custom route for radio player
app.MapControllerRoute(
    name: "radio",
    pattern: "radio",
    defaults: new { controller = "Song", action = "Index" });

// Custom route for song search
app.MapControllerRoute(
    name: "song-search",
    pattern: "radio/search",
    defaults: new { controller = "Song", action = "Search" });

app.MapControllerRoute(
	name: "default",
	pattern: "{controller=Home}/{action=Index}/{id?}");

app.Run();
