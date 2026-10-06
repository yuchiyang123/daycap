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
builder.Services.AddScoped<IAccountService, AccountService>();
builder.Services.AddScoped<IAssetService, AssetService>();
builder.Services.AddScoped<INotificationService, NotificationService>();
builder.Services.AddScoped<IMonthEndService, MonthEndService>();
builder.Services.AddScoped<DayCap.Api.Services.Onboarding.IOnboardingService, DayCap.Api.Services.Onboarding.OnboardingService>();
builder.Services.AddScoped<DayCap.Api.Services.Push.IPushService, DayCap.Api.Services.Push.PushService>();
builder.Services.AddScoped<IJarService, JarService>();
builder.Services.AddScoped<IDebtService, DebtService>();
builder.Services.AddScoped<IInstallmentService, InstallmentService>();
builder.Services.AddScoped<ISessionService, SessionService>();
builder.Services.AddScoped<IUserDataService, UserDataService>();
builder.Services.AddScoped<ITripService, TripService>();
builder.Services.AddScoped<IRunwayService, RunwayService>();
if (!builder.Environment.IsEnvironment("Testing")) builder.Services.AddHostedService<DayCap.Api.Services.Push.NightlyPushWorker>();
builder.Services.AddScoped<IAutoMoneyService, AutoMoneyService>();
if (!builder.Environment.IsEnvironment("Testing")) builder.Services.AddHostedService<AutoMoneyWorker>();

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
                },
                // §21.3：用 DayCap 自己的裝置 cookie 記下登入中的裝置；被撤銷的裝置、或在「登出其他所有裝置」之前發的 token 拒絕。
                // 被拒的回應帶 X-DayCap-Revoked，前端會呼叫 Mini-SSO 登出（連 refresh token 一起撤銷）。
                OnTokenValidated = async context =>
                {
                    var http = context.HttpContext;
                    if (context.Principal is null) return;
                    var userId = DayCap.Api.Common.ClaimsPrincipalExtensions.GetUserId(context.Principal);
                    if (!context.Request.Cookies.TryGetValue(SessionService.DeviceCookie, out var device) || device.Length is < 16 or > 64)
                    {
                        device = SessionService.NewDeviceId();
                        http.Response.Cookies.Append(SessionService.DeviceCookie, device, new CookieOptions
                        {
                            HttpOnly = true, Secure = context.Request.IsHttps, SameSite = SameSiteMode.Lax, Path = "/",
                            MaxAge = TimeSpan.FromDays(400), IsEssential = true,
                        });
                    }
                    http.Items[SessionService.DeviceCookie] = device;
                    var issued = (context.SecurityToken as Microsoft.IdentityModel.JsonWebTokens.JsonWebToken)?.IssuedAt;
                    var sessions = http.RequestServices.GetRequiredService<ISessionService>();
                    var ok = await sessions.CheckAsync(userId, device, issued is { } i && i > DateTime.MinValue ? i : null,
                        context.Request.Headers.UserAgent.ToString(), http.RequestAborted);
                    if (!ok)
                    {
                        http.Items["daycap.revoked"] = true;
                        http.Response.Cookies.Delete(SessionService.DeviceCookie, new CookieOptions { Path = "/" });
                        context.Fail("這台裝置已經被登出。");
                    }
                },
                OnChallenge = context =>
                {
                    if (context.HttpContext.Items.ContainsKey("daycap.revoked")) context.Response.Headers["X-DayCap-Revoked"] = "1";
                    return Task.CompletedTask;
                },
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
