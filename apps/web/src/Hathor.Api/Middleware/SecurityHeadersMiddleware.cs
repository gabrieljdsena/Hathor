namespace Hathor.Api.Middleware;

// Defense-in-depth response headers (no cookies in this app — auth is
// Bearer header + ?token= query — so this targets XSS/clickjacking MIME
// sniffing and ?token= leakage through Referer).
//
// Policy notes:
// - Built SPA ships zero inline scripts/styles (dist/index.html references
//   only /assets/*), so scripts stay 'self'-only, except the YouTube IFrame
//   Player API (preview error detection: it reports embed errors like 153
//   that oEmbed cannot predict, so the player falls back to direct audio).
//   React sets style
//   attributes at runtime (background image, cover art) and Tailwind emits
//   a stylesheet, hence 'unsafe-inline' for styles only (no script execution).
// - img-src allows data:/blob:/https: (embedded covers, iTunes/YouTube
//   artwork over https) plus 'self' (API art, background file).
// - media-src 'self' blob: (same-origin range streams; blob for engine use)
//   plus Google video CDN (direct-audio preview fallback for
//   embedding-disabled videos — the ONLY cross-origin media source).
// - connect-src 'self' ws: wss: (same-origin API + SignalR negotiate/fetch
//   plus the ws/wss upgrade; the dev Vite proxy keeps same-origin).
// - frame-src: the ONLY cross-origin frames the app embeds — YouTube
//   preview iframes (Download + Discover views). Without this, frame-src
//   falls back to default-src 'self' and every preview breaks.
// - frame-ancestors 'none' + X-Frame-Options DENY: nothing embeds this UI.
// - No CORP/COEP: external clients (Stream Deck, Home Assistant, mobile)
//   fetch the API cross-origin — CORP same-origin would break <audio>
//   embeds of streams on other origins.
// - HSTS only on HTTPS (local/docker plain-HTTP deploys must not send it).
public sealed class SecurityHeadersMiddleware(RequestDelegate next)
{
    // Single source of truth so tests + nginx.conf can track it.
public const string ContentSecurityPolicy =
    "default-src 'self'; " +
    "script-src 'self' https://www.youtube.com; " +
    "style-src 'self' 'unsafe-inline'; " +
    "img-src 'self' data: blob: https:; " +
    "media-src 'self' blob: https://*.googlevideo.com; " +
    "font-src 'self' data:; " +
    "connect-src 'self' ws: wss:; " +
    "frame-src 'self' https://www.youtube.com https://www.youtube-nocookie.com; " +
    "object-src 'none'; " +
    "base-uri 'self'; " +
    "form-action 'self'; " +
    "frame-ancestors 'none'";

    public async Task InvokeAsync(HttpContext context)
    {
        // Set synchronously (not OnStarting): nothing downstream clears
        // headers, and this stays unit-testable over DefaultHttpContext.
        var headers = context.Response.Headers;
        headers["X-Content-Type-Options"] = "nosniff";
        headers["Referrer-Policy"] = "no-referrer";
        headers["X-Frame-Options"] = "DENY";
        headers["Permissions-Policy"] = "camera=(), microphone=(), geolocation=(), payment=(), usb=()";
        headers["Cross-Origin-Opener-Policy"] = "same-origin";
        headers["Content-Security-Policy"] = ContentSecurityPolicy;
        if (context.Request.IsHttps)
            headers["Strict-Transport-Security"] = "max-age=31536000; includeSubDomains";
        await next(context);
    }
}
