// Copyright 2026 Andre Kaufmann
// SPDX-License-Identifier: Apache-2.0

using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;

namespace Knotarium.Api;

/// <summary>
/// Serves the directory entry point of the bundled offline help at <c>/help/</c>.
///
/// <para>Every file under <c>wwwroot/help</c> is already served by the SPA's static-file
/// registration, and correctly picks up its "no-cache" branch — help asset names are not
/// content-hashed, so they must revalidate. Only the DIRECTORY form needs an endpoint: routing runs
/// at the very start of the pipeline and the SPA's <c>MapFallbackToFile</c> matches
/// <c>{*path:nonfile}</c>, i.e. anything without a file extension, which includes <c>/help/</c>.
/// Once an endpoint is selected StaticFileMiddleware deliberately stands down, so without this the
/// documentation URL would render the application shell instead.</para>
///
/// <para>Routing treats <c>/help</c> and <c>/help/</c> as the same template, so one endpoint covers
/// both and branches on the real path. The trailing slash is not cosmetic: the help's links are
/// relative, and without it they resolve against <c>/</c> instead of <c>/help/</c>.</para>
///
/// <para>Anonymous by design. The help contains no instance data, and the moment someone is most
/// likely to need it is while looking at the sign-in screen.</para>
/// </summary>
public static class HelpEndpoints
{
    /// <param name="helpIndexPath">
    /// Absolute path to <c>wwwroot/help/index.html</c>. Passed in rather than resolved here because
    /// the composition root already probes for the wwwroot location.
    /// </param>
    public static void MapHelpEndpoints(this WebApplication app, string helpIndexPath)
    {
        app.MapGet("/help", (HttpContext ctx) =>
                ctx.Request.Path.Value?.EndsWith('/') == true
                    ? Results.File(helpIndexPath, "text/html; charset=utf-8")
                    : Results.Redirect("/help/", permanent: false))
           .AllowAnonymous();
    }
}
