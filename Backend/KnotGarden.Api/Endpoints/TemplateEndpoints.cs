using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;

namespace KnotGarden.Api;

/// <summary>
/// Shareable-workflow template endpoints (.kgtpl): export/inspect/install/payload for uploaded
/// templates, the read-only built-in gallery, and the persisted user library (save/list/install/
/// delete). Upload parsing and credential/parameter binding are shared via the private helpers.
/// </summary>
public static class TemplateEndpoints
{
    public static void MapTemplateEndpoints(this WebApplication app)
    {
        app.MapPost("/api/templates/export", async (
            KnotGarden.Features.Templates.TemplateExportRequest request,
            KnotGarden.Features.Templates.TemplateExportService exportService,
            HttpContext httpContext,
            CancellationToken cancellationToken) =>
        {
            if (request is null || string.IsNullOrWhiteSpace(request.WorkflowId))
            {
                return Results.BadRequest(new { message = "A workflowId is required." });
            }

            try
            {
                var result = await exportService.ExportAsync(request, cancellationToken);
                if (result is null)
                {
                    return Results.NotFound(new { message = "No version available to export for this workflow." });
                }

                // Surface the portabilization report alongside the file download via a custom header so the
                // exporter UI can show what credential references were lifted into slots before sharing.
                var portabilizationJson = JsonSerializer.Serialize(new
                {
                    slots = result.Report.Slots,
                    rewrittenPaths = result.Report.RewrittenPaths,
                });
                httpContext.Response.Headers["X-Template-Portabilization"] = portabilizationJson;
                httpContext.Response.Headers["Access-Control-Expose-Headers"] = "X-Template-Portabilization";

                // Human-readable, ASCII-safe download name from the template *name* (not the internal id),
                // e.g. "nightly-db-backup-1.3.2.kgtpl". ASCII-only keeps Content-Disposition to a single,
                // unambiguous filename= token (no filename*=UTF-8'' variant for clients to mis-parse).
                var slug = TemplateFileNameSlug(result.Manifest.Name);
                var fileName = $"{slug}-{result.Manifest.TemplateVersion}{KnotGarden.Features.Templates.TemplateFormat.Extension}";
                return Results.File(result.Bytes, KnotGarden.Features.Templates.TemplateFormat.ContentType, fileName);
            }
            catch (KnotGarden.Features.Templates.TemplateExportException ex)
            {
                return Results.BadRequest(new { message = ex.Message });
            }
            catch (KnotGarden.Features.Templates.TemplateParameterException ex)
            {
                return Results.BadRequest(new { message = ex.Message, errors = ex.Errors });
            }
        });

        app.MapPost("/api/templates/inspect", async (
            HttpRequest request,
            KnotGarden.Features.Templates.TemplateInspectService inspectService,
            CancellationToken cancellationToken) =>
        {
            var (bytes, error) = await ReadTemplateUploadAsync(request, cancellationToken);
            if (error is not null)
            {
                return Results.BadRequest(new { message = error });
            }

            try
            {
                var result = await inspectService.InspectAsync(bytes!, cancellationToken);
                return Results.Ok(new
                {
                    manifest = result.Manifest,
                    credentialSlots = result.CredentialSlots,
                    compatibility = result.Compatibility,
                    privilegedNodes = result.PrivilegedNodes,
                });
            }
            catch (KnotGarden.Features.Templates.TemplateArchiveException ex)
            {
                return Results.BadRequest(new { message = ex.Message });
            }
        });

        app.MapPost("/api/templates/install", async (
            HttpRequest request,
            KnotGarden.Features.Templates.TemplateInstallService installService,
            CancellationToken cancellationToken) =>
        {
            var (bytes, error) = await ReadTemplateUploadAsync(request, cancellationToken);
            if (error is not null)
            {
                return Results.BadRequest(new { message = error });
            }

            var form = await request.ReadFormAsync(cancellationToken);
            var (bindings, bindingsError) = ReadCredentialBindings(form);
            if (bindingsError is not null)
            {
                return Results.BadRequest(new { message = bindingsError });
            }

            var (parameterValues, parametersError) = ReadParameterValues(form);
            if (parametersError is not null)
            {
                return Results.BadRequest(new { message = parametersError });
            }

            var workflowName = form["workflowName"].ToString();
            try
            {
                var result = await installService.InstallAsync(bytes!, bindings, workflowName, parameterValues, cancellationToken);
                return Results.Ok(result);
            }
            catch (KnotGarden.Features.Templates.TemplateArchiveException ex)
            {
                return Results.BadRequest(new { message = ex.Message });
            }
            catch (KnotGarden.Features.Templates.TemplateBindingException ex)
            {
                return Results.BadRequest(new { message = ex.Message, errors = ex.Errors });
            }
            catch (KnotGarden.Features.Templates.TemplateParameterException ex)
            {
                return Results.BadRequest(new { message = ex.Message, errors = ex.Errors });
            }
        });

        // Return a template's node/edge graph (+ slots + compatibility) WITHOUT creating a workflow — used to
        // insert a template into the currently open workflow on the canvas.
        app.MapPost("/api/templates/payload", async (
            HttpRequest request,
            KnotGarden.Features.Templates.TemplatePayloadService payloadService,
            CancellationToken cancellationToken) =>
        {
            var (bytes, error) = await ReadTemplateUploadAsync(request, cancellationToken);
            if (error is not null)
            {
                return Results.BadRequest(new { message = error });
            }

            var (parameterValues, parametersError) = ReadParameterValues(await request.ReadFormAsync(cancellationToken));
            if (parametersError is not null)
            {
                return Results.BadRequest(new { message = parametersError });
            }

            try
            {
                var payload = await payloadService.GetPayloadAsync(bytes!, parameterValues, cancellationToken);
                return Results.Ok(new
                {
                    manifest = payload.Manifest,
                    credentialSlots = payload.CredentialSlots,
                    compatibility = payload.Compatibility,
                    nodes = payload.Content.Nodes,
                    edges = payload.Content.Edges,
                });
            }
            catch (KnotGarden.Features.Templates.TemplateArchiveException ex)
            {
                return Results.BadRequest(new { message = ex.Message });
            }
            catch (KnotGarden.Features.Templates.TemplateParameterException ex)
            {
                return Results.BadRequest(new { message = ex.Message, errors = ex.Errors });
            }
        });

        app.MapGet("/api/templates/gallery", async (
            KnotGarden.Features.Templates.BuiltInTemplateGallery gallery,
            CancellationToken cancellationToken) =>
        {
            var templates = await gallery.ListAsync(cancellationToken);
            return Results.Ok(templates);
        });

        app.MapGet("/api/templates/gallery/{templateId}/payload", async (
            string templateId,
            string? parameterValues,
            KnotGarden.Features.Templates.BuiltInTemplateGallery gallery,
            KnotGarden.Features.Templates.TemplatePayloadService payloadService,
            CancellationToken cancellationToken) =>
        {
            var bytes = await gallery.GetArchiveBytesAsync(templateId, cancellationToken);
            if (bytes is null)
            {
                return Results.NotFound(new { message = $"No built-in template '{templateId}'." });
            }

            var (values, valuesError) = ParseParameterValuesJson(parameterValues);
            if (valuesError is not null)
            {
                return Results.BadRequest(new { message = valuesError });
            }

            try
            {
                var payload = await payloadService.GetPayloadAsync(bytes, values, cancellationToken);
                return Results.Ok(new
                {
                    manifest = payload.Manifest,
                    credentialSlots = payload.CredentialSlots,
                    compatibility = payload.Compatibility,
                    nodes = payload.Content.Nodes,
                    edges = payload.Content.Edges,
                });
            }
            catch (KnotGarden.Features.Templates.TemplateParameterException ex)
            {
                return Results.BadRequest(new { message = ex.Message, errors = ex.Errors });
            }
        });

        app.MapGet("/api/templates/gallery/{templateId}", async (
            string templateId,
            KnotGarden.Features.Templates.BuiltInTemplateGallery gallery,
            CancellationToken cancellationToken) =>
        {
            var template = await gallery.GetAsync(templateId, cancellationToken);
            return template is null
                ? Results.NotFound(new { message = $"No built-in template '{templateId}'." })
                : Results.Ok(template);
        });

        app.MapPost("/api/templates/gallery/{templateId}/install", async (
            string templateId,
            HttpRequest request,
            KnotGarden.Features.Templates.BuiltInTemplateGallery gallery,
            KnotGarden.Features.Templates.TemplateInstallService installService,
            CancellationToken cancellationToken) =>
        {
            var bytes = await gallery.GetArchiveBytesAsync(templateId, cancellationToken);
            if (bytes is null)
            {
                return Results.NotFound(new { message = $"No built-in template '{templateId}'." });
            }

            IReadOnlyDictionary<string, string>? bindings = null;
            IReadOnlyDictionary<string, string>? parameterValues = null;
            string workflowName = string.Empty;
            if (request.HasFormContentType)
            {
                var form = await request.ReadFormAsync(cancellationToken);
                var (parsed, bindingsError) = ReadCredentialBindings(form);
                if (bindingsError is not null)
                {
                    return Results.BadRequest(new { message = bindingsError });
                }

                var (parsedParameters, parametersError) = ReadParameterValues(form);
                if (parametersError is not null)
                {
                    return Results.BadRequest(new { message = parametersError });
                }

                bindings = parsed;
                parameterValues = parsedParameters;
                workflowName = form["workflowName"].ToString();
            }

            try
            {
                var result = await installService.InstallAsync(bytes, bindings, workflowName, parameterValues, cancellationToken);
                return Results.Ok(result);
            }
            catch (KnotGarden.Features.Templates.TemplateArchiveException ex)
            {
                return Results.BadRequest(new { message = ex.Message });
            }
            catch (KnotGarden.Features.Templates.TemplateBindingException ex)
            {
                return Results.BadRequest(new { message = ex.Message, errors = ex.Errors });
            }
            catch (KnotGarden.Features.Templates.TemplateParameterException ex)
            {
                return Results.BadRequest(new { message = ex.Message, errors = ex.Errors });
            }
        });

        // --- User template library (persisted, manageable) ---

        // Pack the current workflow and save it to this instance's library (upsert by template id).
        app.MapPost("/api/templates/library/save", async (
            KnotGarden.Features.Templates.TemplateExportRequest request,
            KnotGarden.Features.Templates.UserTemplateLibrary library,
            CancellationToken cancellationToken) =>
        {
            if (request is null || string.IsNullOrWhiteSpace(request.WorkflowId))
            {
                return Results.BadRequest(new { message = "A workflowId is required." });
            }

            try
            {
                var saved = await library.SaveAsync(request, cancellationToken);
                return saved is null
                    ? Results.NotFound(new { message = "No version available to save for this workflow." })
                    : Results.Ok(saved);
            }
            catch (KnotGarden.Features.Templates.TemplateExportException ex)
            {
                return Results.BadRequest(new { message = ex.Message });
            }
            catch (KnotGarden.Features.Templates.TemplateParameterException ex)
            {
                return Results.BadRequest(new { message = ex.Message, errors = ex.Errors });
            }
        });

        // Save an already-packed .kgtpl (uploaded on the Import tab) directly into the library.
        app.MapPost("/api/templates/library/save-archive", async (
            HttpRequest request,
            KnotGarden.Features.Templates.UserTemplateLibrary library,
            CancellationToken cancellationToken) =>
        {
            var (bytes, error) = await ReadTemplateUploadAsync(request, cancellationToken);
            if (error is not null)
            {
                return Results.BadRequest(new { message = error });
            }

            try
            {
                var saved = await library.SaveArchiveAsync(bytes!, cancellationToken);
                return Results.Ok(saved);
            }
            catch (KnotGarden.Features.Templates.TemplateArchiveException ex)
            {
                return Results.BadRequest(new { message = ex.Message });
            }
        });

        app.MapGet("/api/templates/library", async (
            KnotGarden.Features.Templates.UserTemplateLibrary library,
            CancellationToken cancellationToken) =>
        {
            var templates = await library.ListAsync(cancellationToken);
            return Results.Ok(templates);
        });

        app.MapGet("/api/templates/library/{templateId}/payload", async (
            string templateId,
            string? parameterValues,
            KnotGarden.Features.Templates.UserTemplateLibrary library,
            KnotGarden.Features.Templates.TemplatePayloadService payloadService,
            CancellationToken cancellationToken) =>
        {
            var bytes = await library.GetArchiveBytesAsync(templateId, cancellationToken);
            if (bytes is null)
            {
                return Results.NotFound(new { message = $"No saved template '{templateId}'." });
            }

            var (values, valuesError) = ParseParameterValuesJson(parameterValues);
            if (valuesError is not null)
            {
                return Results.BadRequest(new { message = valuesError });
            }

            try
            {
                var payload = await payloadService.GetPayloadAsync(bytes, values, cancellationToken);
                return Results.Ok(new
                {
                    manifest = payload.Manifest,
                    credentialSlots = payload.CredentialSlots,
                    compatibility = payload.Compatibility,
                    nodes = payload.Content.Nodes,
                    edges = payload.Content.Edges,
                });
            }
            catch (KnotGarden.Features.Templates.TemplateParameterException ex)
            {
                return Results.BadRequest(new { message = ex.Message, errors = ex.Errors });
            }
        });

        app.MapPost("/api/templates/library/{templateId}/install", async (
            string templateId,
            HttpRequest request,
            KnotGarden.Features.Templates.UserTemplateLibrary library,
            KnotGarden.Features.Templates.TemplateInstallService installService,
            CancellationToken cancellationToken) =>
        {
            var bytes = await library.GetArchiveBytesAsync(templateId, cancellationToken);
            if (bytes is null)
            {
                return Results.NotFound(new { message = $"No saved template '{templateId}'." });
            }

            IReadOnlyDictionary<string, string>? bindings = null;
            IReadOnlyDictionary<string, string>? parameterValues = null;
            string workflowName = string.Empty;
            if (request.HasFormContentType)
            {
                var form = await request.ReadFormAsync(cancellationToken);
                var (parsed, bindingsError) = ReadCredentialBindings(form);
                if (bindingsError is not null)
                {
                    return Results.BadRequest(new { message = bindingsError });
                }

                var (parsedParameters, parametersError) = ReadParameterValues(form);
                if (parametersError is not null)
                {
                    return Results.BadRequest(new { message = parametersError });
                }

                bindings = parsed;
                parameterValues = parsedParameters;
                workflowName = form["workflowName"].ToString();
            }

            try
            {
                var result = await installService.InstallAsync(bytes, bindings, workflowName, parameterValues, cancellationToken);
                return Results.Ok(result);
            }
            catch (KnotGarden.Features.Templates.TemplateArchiveException ex)
            {
                return Results.BadRequest(new { message = ex.Message });
            }
            catch (KnotGarden.Features.Templates.TemplateBindingException ex)
            {
                return Results.BadRequest(new { message = ex.Message, errors = ex.Errors });
            }
            catch (KnotGarden.Features.Templates.TemplateParameterException ex)
            {
                return Results.BadRequest(new { message = ex.Message, errors = ex.Errors });
            }
        });

        app.MapDelete("/api/templates/library/{templateId}", async (
            string templateId,
            KnotGarden.Features.Templates.UserTemplateLibrary library,
            CancellationToken cancellationToken) =>
        {
            var removed = await library.RemoveAsync(templateId, cancellationToken);
            return removed
                ? Results.Ok(new { removed = true })
                : Results.NotFound(new { message = $"No saved template '{templateId}'." });
        });
    }

    // Reads the uploaded .kgtpl file from a multipart 'template' field into bytes; returns an error message
    // (and null bytes) when the request shape is wrong. The form is cached, so callers may re-read it for bindings.
    private static async Task<(byte[]? Bytes, string? Error)> ReadTemplateUploadAsync(HttpRequest request, CancellationToken cancellationToken)
    {
        if (!request.HasFormContentType)
        {
            return (null, "Request must be multipart form-data with a 'template' file.");
        }

        var form = await request.ReadFormAsync(cancellationToken);
        var file = form.Files.GetFile("template");
        if (file is null || file.Length == 0)
        {
            return (null, "No .kgtpl file uploaded under 'template'.");
        }

        using var memory = new MemoryStream();
        await file.CopyToAsync(memory, cancellationToken);
        return (memory.ToArray(), null);
    }

    // Slugifies a template name into an ASCII, lowercase-kebab filename stem (e.g. "Nightly DB Backup"
    // → "nightly-db-backup"), falling back to "template" when nothing usable remains.
    private static string TemplateFileNameSlug(string name)
    {
        var builder = new System.Text.StringBuilder((name ?? string.Empty).Length);
        var lastDash = false;
        foreach (var ch in (name ?? string.Empty).ToLowerInvariant())
        {
            if ((ch >= 'a' && ch <= 'z') || (ch >= '0' && ch <= '9'))
            {
                builder.Append(ch);
                lastDash = false;
            }
            else if (!lastDash && builder.Length > 0)
            {
                builder.Append('-');
                lastDash = true;
            }
        }

        var slug = builder.ToString().Trim('-');
        return slug.Length == 0 ? "template" : slug;
    }

    // Parses the optional 'credentialBindings' form field (a JSON object of slot→credentialId).
    private static (IReadOnlyDictionary<string, string>? Bindings, string? Error) ReadCredentialBindings(IFormCollection form)
    {
        var bindingsJson = form["credentialBindings"].ToString();
        if (string.IsNullOrWhiteSpace(bindingsJson))
        {
            return (null, null);
        }

        try
        {
            return (JsonSerializer.Deserialize<Dictionary<string, string>>(bindingsJson), null);
        }
        catch (JsonException)
        {
            return (null, "'credentialBindings' must be a JSON object of slot→credentialId.");
        }
    }

    // Parses the optional 'parameterValues' form field (a JSON object of parameterKey→string value).
    private static (IReadOnlyDictionary<string, string>? Values, string? Error) ReadParameterValues(IFormCollection form)
        => ParseParameterValuesJson(form["parameterValues"].ToString());

    // Parses an optional parameterValues JSON object (used by both the form field and the GET query string).
    private static (IReadOnlyDictionary<string, string>? Values, string? Error) ParseParameterValuesJson(string? valuesJson)
    {
        if (string.IsNullOrWhiteSpace(valuesJson))
        {
            return (null, null);
        }

        try
        {
            return (JsonSerializer.Deserialize<Dictionary<string, string>>(valuesJson), null);
        }
        catch (JsonException)
        {
            return (null, "'parameterValues' must be a JSON object of parameterKey→value.");
        }
    }
}
