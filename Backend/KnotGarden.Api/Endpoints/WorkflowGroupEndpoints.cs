using System;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using KnotGarden.Core.Domain;
using KnotGarden.Infrastructure.Persistence;

namespace KnotGarden.Api;

/// <summary>
/// Workflow group tree endpoints. Groups are stored in the file store (not the DB) and use
/// ETag / If-Match optimistic concurrency so two editors can't silently clobber the tree.
/// </summary>
public static class WorkflowGroupEndpoints
{
    public static void MapWorkflowGroupEndpoints(this WebApplication app)
    {
        app.MapGet("/api/workflow-groups", async (FileWorkflowStore fileWorkflowStore, HttpContext httpContext) =>
        {
            var (container, etag) = await fileWorkflowStore.GetGroupsWithETagAsync();
            httpContext.Response.Headers.ETag = etag;
            return Results.Ok(container);
        });

        app.MapPut("/api/workflow-groups", async (GroupContainer container, FileWorkflowStore fileWorkflowStore, HttpContext httpContext) =>
        {
            if (!httpContext.Request.Headers.TryGetValue("If-Match", out var ifMatchValues))
            {
                return Results.StatusCode(StatusCodes.Status428PreconditionRequired);
            }

            var ifMatch = ifMatchValues.ToString();
            if (string.IsNullOrWhiteSpace(ifMatch))
            {
                return Results.StatusCode(StatusCodes.Status428PreconditionRequired);
            }

            try
            {
                var newEtag = await fileWorkflowStore.SaveGroupsAsync(container, ifMatch);
                httpContext.Response.Headers.ETag = newEtag;
                return Results.Ok();
            }
            catch (GroupPreconditionFailedException ex)
            {
                return Results.Json(new { message = ex.Message }, statusCode: StatusCodes.Status412PreconditionFailed);
            }
            catch (ArgumentException ex)
            {
                return Results.BadRequest(new { message = ex.Message });
            }
        });

        app.MapDelete("/api/workflow-groups/{id}", async (string id, FileWorkflowStore fileWorkflowStore) =>
        {
            var idRegex = new System.Text.RegularExpressions.Regex("^grp_[a-zA-Z0-9_-]+$");
            if (string.IsNullOrWhiteSpace(id) || !idRegex.IsMatch(id))
            {
                return Results.BadRequest(new { message = "Invalid group ID syntax." });
            }

            await fileWorkflowStore.DeleteGroupAsync(id);
            return Results.NoContent();
        });
    }
}
