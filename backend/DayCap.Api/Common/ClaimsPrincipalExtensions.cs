using System.Security.Claims;

namespace DayCap.Api.Common;

/// <summary>
/// 從 Mini-SSO 簽發的 JWT claims 取出使用者識別碼（跟 eating-map 同一套寫法）。
/// </summary>
public static class ClaimsPrincipalExtensions
{
    public static string GetUserId(this ClaimsPrincipal user) =>
        user.FindFirstValue(ClaimTypes.NameIdentifier)
        ?? user.FindFirstValue("sub")
        ?? throw new InvalidOperationException("JWT 內找不到使用者識別碼 claim。");

    public static string? GetUserName(this ClaimsPrincipal user) =>
        user.FindFirstValue(ClaimTypes.Name)
        ?? user.FindFirstValue("unique_name")
        ?? user.FindFirstValue("name");
}
