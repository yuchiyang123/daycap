using System.Security.Cryptography;
using System.Text;

namespace DayCap.Api.Middleware;

/// <summary>
/// 會改資料的 /api 請求（POST/PUT/PATCH/DELETE）必須帶 X-CSRF-TOKEN，而且要跟 XSRF-TOKEN cookie 相等。
/// cookie 是 Mini-SSO 的 GET /api/auth/csrf 發的（網域 .matthewyu.uk，這個站也讀得到），
/// 所以前端改資料前先確定拿過一次就好。
/// </summary>
public class CsrfMiddleware(RequestDelegate next)
{
    private static readonly HashSet<string> SafeMethods = ["GET", "HEAD", "OPTIONS"];

    public async Task InvokeAsync(HttpContext context)
    {
        if (!SafeMethods.Contains(context.Request.Method) && context.Request.Path.StartsWithSegments("/api"))
        {
            var cookie = context.Request.Cookies["XSRF-TOKEN"];
            var header = context.Request.Headers["X-CSRF-TOKEN"].ToString();
            if (string.IsNullOrEmpty(cookie) || string.IsNullOrEmpty(header) ||
                !CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(cookie), Encoding.UTF8.GetBytes(header)))
            {
                context.Response.StatusCode = StatusCodes.Status403Forbidden;
                await context.Response.WriteAsJsonAsync(new { title = "Invalid CSRF Token" });
                return;
            }
        }
        await next(context);
    }
}
