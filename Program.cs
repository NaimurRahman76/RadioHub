using Microsoft.EntityFrameworkCore;
using RadioStation.Data;
using RadioStation.Repository;
using RadioStation.Services;
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

// Register the song queue and streaming services as singletons
builder.Services.AddSingleton<SongQueueService>();
builder.Services.AddSingleton(provider =>
{
    var config = provider.GetRequiredService<IConfiguration>();
    var icecastUrl = config.GetValue<string>("Icecast:Url");
    var icecastMountpoint = config.GetValue<string>("Icecast:Mountpoint");
    var icecastPassword = config.GetValue<string>("Icecast:Password");
    var logger = provider.GetRequiredService<ILogger<StreamingService>>();
    return new StreamingService(icecastUrl, icecastMountpoint, icecastPassword, logger);
});

// Add the background service for playing songs
builder.Services.AddHostedService<SongPlayerService>();
builder.Services.AddSignalR();
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

app.UseEndpoints(endpoints =>
{
    endpoints.MapHub<RadioHub>("/radioHub");
    // ... other endpoints
});

app.MapControllerRoute(
	name: "default",
	pattern: "{controller=Home}/{action=Index}/{id?}");

app.Run();
