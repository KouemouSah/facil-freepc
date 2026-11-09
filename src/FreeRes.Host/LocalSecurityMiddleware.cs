using Microsoft.AspNetCore.Http;

namespace FreeRes.Host;

/// <summary>
/// Barriere de securite de l'interface locale. Un serveur sur 127.0.0.1 n'est PAS prive : n'importe quelle
/// page web ouverte dans un navigateur peut lui envoyer des requetes. Trois controles, dans cet ordre :
///   1. Host = 127.0.0.1[:port]        -> bloque le DNS rebinding ;
///   2. cookie de session = jeton       -> seule la fenetre WebView2 (qui l'a pose) est autorisee ;
///   3. methode non sure => Origin/Sec-Fetch-Site du meme site -> bloque les formulaires inter-sites (CSRF).
/// </summary>
public sealed class LocalSecurityMiddleware(RequestDelegate next, LocalWebHostOptions options)
{
    public async Task InvokeAsync(HttpContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        var request = context.Request;

        if (!string.Equals(request.Host.Host, LocalWebHostOptions.LoopbackHost, StringComparison.Ordinal))
        {
            await Reject(context, StatusCodes.Status400BadRequest, "hote refuse").ConfigureAwait(false);
            return;
        }

        if (!SessionToken.Matches(request.Cookies[LocalWebHostOptions.TokenCookieName], options.Token))
        {
            await Reject(context, StatusCodes.Status401Unauthorized, "session requise").ConfigureAwait(false);
            return;
        }

        if (!HttpMethods.IsGet(request.Method) && !HttpMethods.IsHead(request.Method) && !IsSameOrigin(request))
        {
            await Reject(context, StatusCodes.Status403Forbidden, "origine refusee").ConfigureAwait(false);
            return;
        }

        var headers = context.Response.Headers;
        headers.ContentSecurityPolicy = "default-src 'self'; script-src 'self'; style-src 'self'; img-src 'self' data:; connect-src 'self'; frame-ancestors 'none'; base-uri 'none'; form-action 'self'";
        headers.XContentTypeOptions = "nosniff";
        headers["Referrer-Policy"] = "no-referrer";
        headers.XFrameOptions = "DENY";
        headers.CacheControl = "no-store";

        await next(context).ConfigureAwait(false);
    }

    /// <summary>Une requete modifiante doit venir de la page elle-meme (meme schema, hote et port).</summary>
    internal static bool IsSameOrigin(HttpRequest request)
    {
        var fetchSite = request.Headers["Sec-Fetch-Site"].ToString();
        if (fetchSite.Length > 0 && !string.Equals(fetchSite, "same-origin", StringComparison.Ordinal))
        {
            return false;
        }

        var origin = request.Headers.Origin.ToString();
        if (origin.Length == 0 || !Uri.TryCreate(origin, UriKind.Absolute, out var uri))
        {
            return false;   // pas d'Origin sur un POST = client non navigateur ou politique stricte : on refuse
        }

        return string.Equals(uri.Scheme, request.Scheme, StringComparison.OrdinalIgnoreCase)
            && string.Equals(uri.Host, request.Host.Host, StringComparison.Ordinal)
            && uri.Port == (request.Host.Port ?? (request.IsHttps ? 443 : 80));
    }

    private static Task Reject(HttpContext context, int status, string reason)
    {
        context.Response.StatusCode = status;
        context.Response.ContentType = "text/plain; charset=utf-8";
        return context.Response.WriteAsync(reason);
    }
}
