using System.Text;
using System.Text.Json.Serialization;
using DayCap.Api.Auth;
using DayCap.Api.Data;
using DayCap.Api.Middleware;
using DayCap.Api.Services;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.EntityFrameworkCore;
using Microsoft.IdentityModel.Tokens;

// 備份模式：dotnet DayCap.Api.dll backup <目標檔>，做完就結束，不啟動網站。
if (args is ["backup", var backupTarget])
{
    var path = Environment.GetEnvironmentVariable("Database__Path");
    if (string.IsNullOrWhiteSpace(path)) path = Path.Combine(AppContext.BaseDirectory, "App_Data", "daycap.db");
    return SqliteBackup.Run(path, backupTarget);
}

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddControllers()
    .AddJsonOptions(o => o.JsonSerializerOptions.Converters.Add(new JsonStringEnumConverter()));

var dbPath = builder.Configuration["Database:Path"];
if (string.IsNullOrWhiteSpace(dbPath)) dbPath = Path.Combine(builder.Environment.ContentRootPath, "App_Data", "daycap.db");
Directory.CreateDirectory(Path.GetDirectoryName(dbPath)!);
builder.Services.AddDbContext<DayCapDbContext>(o => o.UseSqlite($"Data Source={dbPath}"));

builder.Services.AddSingleton<IAppClock, TaipeiClock>();
builder.Services.AddScoped<ISettingsService, SettingsService>();
builder.Services.AddScoped<ICalendarService, CalendarService>();
builder.Services.AddScoped<IPeriodService, PeriodService>();
builder.Services.AddScoped<IEntryService, EntryService>();
builder.Services.AddScoped<IQuoteService, QuoteService>();
builder.Services.AddScoped<IAssetService, AssetService>();
builder.Services.AddScoped<INotificationService, NotificationService>();

builder.Services.AddHttpClient(CalendarService.HttpClientName, c => c.Timeout = TimeSpan.FromSeconds(10));
builder.Services.AddHttpClient(QuoteService.HttpClientName, c =>
{
    c.Timeout = TimeSpan.FromSeconds(15);
    c.DefaultRequestHeaders.UserAgent.ParseAdd("DayCap/1.0");
});

builder.Services.AddHealthChecks().AddDbContextCheck<DayCapDbContext>();
builder.Services.AddExceptionHandler<GlobalExceptionHandler>();
builder.Services.AddProblemDetails();

// 前面有 cloudflared + nginx 兩層代理，來源 IP / https 判斷要吃 forwarded header。
builder.Services.Configure<ForwardedHeadersOptions>(o =>
{
    o.ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto;
    o.KnownNetworks.Clear();
    o.KnownProxies.Clear();
});

var useDevAuth = builder.Environment.IsDevelopment() && builder.Configuration.GetValue<bool>("DevAuth:Enabled");
if (useDevAuth)
{
    builder.Services.AddAuthentication(DevAuthHandler.SchemeName)
        .AddScheme<AuthenticationSchemeOptions, DevAuthHandler>(DevAuthHandler.SchemeName, null);
}
else
{
    // 跟 eating-map / Todo-List 一樣：信任 Mini-SSO 簽的 JWT（共用 Jwt:Key/Issuer/Audience），
    // 從 HttpOnly 的 "token" cookie 讀，不走 Authorization header。
    builder.Services.AddAuthentication(JwtBearerDefaults.AuthenticationScheme)
        .AddJwtBearer(options =>
        {
            var jwt = builder.Configuration.GetSection("Jwt");
            var key = jwt["Key"];
            if (string.IsNullOrWhiteSpace(key) || key.Length < 32)
                throw new InvalidOperationException("Jwt:Key 未設定或太短，要跟 Mini-SSO 的 JWT_KEY 一致。");

            options.TokenValidationParameters = new TokenValidationParameters
            {
                ValidateIssuer = true,
                ValidIssuer = jwt["Issuer"],
                ValidateAudience = true,
                ValidAudience = jwt["Audience"],
                ValidateIssuerSigningKey = true,
                IssuerSigningKey = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(key)),
                ValidateLifetime = true,
                ClockSkew = TimeSpan.FromSeconds(30)
            };
            options.Events = new JwtBearerEvents
            {
                OnMessageReceived = context =>
                {
                    if (context.Request.Cookies.TryGetValue("token", out var token)) context.Token = token;
                    return Task.CompletedTask;
                }
            };
        });
}
builder.Services.AddAuthorization();

var app = builder.Build();

using (var scope = app.Services.CreateScope())
{
    scope.ServiceProvider.GetRequiredService<DayCapDbContext>().Database.Migrate();
}

app.UseForwardedHeaders();
app.UseExceptionHandler();

// 雙提交 CSRF：跟 Mini-SSO 同一套規則，X-CSRF-TOKEN header 要等於 XSRF-TOKEN cookie。
// 本機 DevAuth 沒有 Mini-SSO 發 cookie，所以只在正式模式檢查。
if (!useDevAuth)
{
    app.UseMiddleware<CsrfMiddleware>();
}

app.UseAuthentication();
app.UseAuthorization();

app.MapControllers();
app.MapHealthChecks("/healthz");

app.Run();
return 0;

public partial class Program { }
