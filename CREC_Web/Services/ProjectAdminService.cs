using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;

namespace CREC_Web.Services;

/// <summary>Operator token authentication, with short-lived, HttpOnly browser sessions.</summary>
public sealed class ProjectAdminService
{
    private readonly byte[] _tokenHash;
    private readonly ConcurrentDictionary<string, DateTimeOffset> _sessions = new();

    public ProjectAdminService(string token) => _tokenHash = Hash(token);

    public bool ValidateToken(string? token) => !string.IsNullOrEmpty(token) && token.Length <= 512
        && CryptographicOperations.FixedTimeEquals(_tokenHash, Hash(token));

    public bool IsAuthorized(HttpContext context)
    {
        if (ValidateToken(context.Request.Headers["X-CREC-Admin"].ToString())) return true;
        var cookie = context.Request.Cookies[CookieName(context)];
        return cookie is not null && _sessions.TryGetValue(cookie, out var expiry) && expiry > DateTimeOffset.UtcNow;
    }

    public void SignIn(HttpContext context)
    {
        foreach (var pair in _sessions.Where(pair => pair.Value <= DateTimeOffset.UtcNow))
            _sessions.TryRemove(pair.Key, out _);
        var session = Convert.ToHexString(RandomNumberGenerator.GetBytes(32));
        var expiry = DateTimeOffset.UtcNow.AddHours(8);
        _sessions[session] = expiry;
        context.Response.Cookies.Append(CookieName(context), session, new CookieOptions
        {
            HttpOnly = true, Secure = context.Request.IsHttps, SameSite = SameSiteMode.Strict,
            Path = "/api/projects", Expires = expiry, IsEssential = true
        });
    }

    private static string CookieName(HttpContext context) => "crec-admin-" + context.Request.Host.Port;
    private static byte[] Hash(string value) => SHA256.HashData(Encoding.UTF8.GetBytes(value));

    public static bool IsSameOrigin(HttpRequest request)
    {
        var site = request.Headers["Sec-Fetch-Site"].ToString();
        if (site.Length > 0 && site != "same-origin" && site != "none") return false;
        var origin = request.Headers.Origin.ToString();
        return origin.Length == 0 || (Uri.TryCreate(origin, UriKind.Absolute, out var source)
            && Uri.TryCreate($"{request.Scheme}://{request.Host}", UriKind.Absolute, out var target)
            && source.GetLeftPart(UriPartial.Authority).Equals(target.GetLeftPart(UriPartial.Authority), StringComparison.OrdinalIgnoreCase));
    }
}
