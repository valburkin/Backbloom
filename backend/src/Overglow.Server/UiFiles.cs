using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.StaticFiles;
using Microsoft.Extensions.FileProviders;
using Microsoft.Net.Http.Headers;

namespace Overglow.Server;

/// <summary>
/// Serves the built React UI (<c>ui/dist</c>): files by path, <c>index.html</c> for <c>/</c> and for any other
/// GET of a path without a file extension (single-page app fallback; the UI routes by URL hash today, so this keeps
/// bookmarks of future path routes working). Vite names everything under <c>assets/</c> by content hash: those are
/// cached for a year as immutable, everything else is revalidated on every load (<c>no-cache</c>, with ETag), so a new
/// version shows up at once.
/// </summary>
public static class UiFiles
{
    public const string ImmutableCacheControl = "public, max-age=31536000, immutable";
    public const string RevalidateCacheControl = "no-cache";

    public static WebApplication UseUiFiles(this WebApplication app, string directory)
    {
        ArgumentNullException.ThrowIfNull(app);
        ArgumentException.ThrowIfNullOrEmpty(directory);
        var files = new PhysicalFileProvider(Path.GetFullPath(directory));
        var types = new FileExtensionContentTypeProvider();
        types.Mappings[".webmanifest"] = "application/manifest+json";

        app.UseDefaultFiles(new DefaultFilesOptions { FileProvider = files });
        app.UseStaticFiles(new StaticFileOptions
        {
            FileProvider = files,
            ContentTypeProvider = types,
            OnPrepareResponse = context => context.Context.Response.Headers[HeaderNames.CacheControl] = CacheControlFor(context.Context.Request.Path),
        });

        var index = files.GetFileInfo("index.html");
        app.Use(async (context, next) =>
        {
            if (!IsFallbackRequest(context.Request) || !index.Exists)
            {
                await next(context);
                return;
            }

            context.Response.ContentType = "text/html; charset=utf-8";
            context.Response.Headers[HeaderNames.CacheControl] = RevalidateCacheControl;
            context.Response.ContentLength = index.Length;
            if (HttpMethods.IsGet(context.Request.Method))
            {
                await context.Response.SendFileAsync(index, context.RequestAborted);
            }
        });
        return app;
    }

    internal static string CacheControlFor(PathString path) =>
        path.StartsWithSegments("/assets", StringComparison.OrdinalIgnoreCase) ? ImmutableCacheControl : RevalidateCacheControl;

    // GET/HEAD of a path that names no file (no extension, not under assets/); other misses stay 404.
    internal static bool IsFallbackRequest(HttpRequest request)
    {
        if (!HttpMethods.IsGet(request.Method) && !HttpMethods.IsHead(request.Method))
        {
            return false;
        }

        var path = request.Path.Value ?? "/";
        return !request.Path.StartsWithSegments("/assets", StringComparison.OrdinalIgnoreCase) && !Path.HasExtension(path);
    }
}
