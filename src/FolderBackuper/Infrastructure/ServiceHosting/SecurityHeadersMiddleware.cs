namespace FolderBackuper.Infrastructure.ServiceHosting;

public sealed class SecurityHeadersMiddleware(RequestDelegate next)
{
    public const string ContentSecurityPolicy =
        "default-src 'self'; base-uri 'self'; frame-ancestors 'none'; " +
        "form-action 'self'; img-src 'self' data:; font-src 'self' data:; " +
        "style-src 'self' 'unsafe-inline'; script-src 'self' 'unsafe-inline'";

    public Task InvokeAsync(HttpContext context)
    {
        Apply(context.Response.Headers);
        return next(context);
    }

    public static void Apply(IHeaderDictionary headers)
    {
        headers.ContentSecurityPolicy = ContentSecurityPolicy;
        headers.XContentTypeOptions = "nosniff";
        headers.XFrameOptions = "DENY";
        headers["Referrer-Policy"] = "no-referrer";
        headers.Append("Permissions-Policy", "camera=(), microphone=(), geolocation=()");
    }
}
