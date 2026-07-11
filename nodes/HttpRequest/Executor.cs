using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using KnotGarden.Core.Contracts;
using KnotGarden.Core.Domain;

namespace KnotGarden.Nodes;

public class HttpRequestExecutor : INodeExecutor
{
    public async ValueTask<NodeResult> ExecuteAsync(
        NodeInput input,
        INodeContext context,
        CancellationToken cancellationToken)
    {
        if (context.Http == null)
        {
            return new NodeResult("error", 
                JsonSerializer.SerializeToElement(new Dictionary<string, object> { ["error"] = "HTTP capability not granted or client missing." }), 
                NodeExecutionStatus.Failed);
        }

        string url = "";
        if (input.Parameters.TryGetValue("url", out var urlElem))
        {
            url = urlElem.GetString() ?? "";
        }

        if (string.IsNullOrEmpty(url))
        {
            return new NodeResult("error", 
                JsonSerializer.SerializeToElement(new Dictionary<string, object> { ["error"] = "Missing required url parameter." }), 
                NodeExecutionStatus.Failed);
        }

        string methodStr = "GET";
        if (input.Parameters.TryGetValue("method", out var methodElem))
        {
            methodStr = methodElem.GetString() ?? "GET";
        }

        string bodyStr = "";
        if (input.Parameters.TryGetValue("body", out var bodyElem))
        {
            bodyStr = bodyElem.ValueKind == JsonValueKind.String ? bodyElem.GetString() ?? "" : bodyElem.GetRawText();
        }

        string headersStr = "";
        if (input.Parameters.TryGetValue("headers", out var headersElem))
        {
            headersStr = headersElem.ValueKind == JsonValueKind.String ? headersElem.GetString() ?? "" : headersElem.GetRawText();
        }

        using var request = new HttpRequestMessage(new HttpMethod(methodStr), url);

        if (!string.IsNullOrEmpty(bodyStr))
        {
            request.Content = new StringContent(bodyStr, System.Text.Encoding.UTF8, "application/json");
        }

        if (!string.IsNullOrEmpty(headersStr))
        {
            try
            {
                var headersDict = JsonSerializer.Deserialize<Dictionary<string, string>>(headersStr);
                if (headersDict != null)
                {
                    foreach (var kvp in headersDict)
                    {
                        request.Headers.TryAddWithoutValidation(kvp.Key, kvp.Value);
                    }
                }
            }
            catch
            {
                // Ignore malformed headers
            }
        }

        if (input.Parameters.TryGetValue("apiKeySecretRef", out var secretRefElem) && context.Credentials != null)
        {
            var secretRef = secretRefElem.GetString();
            if (!string.IsNullOrEmpty(secretRef))
            {
                var secretVal = await context.Credentials.GetSecretAsync(secretRef, cancellationToken);
                if (!string.IsNullOrEmpty(secretVal))
                {
                    request.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", secretVal);
                }
            }
        }

        try
        {
            var response = await context.Http.SendAsync(request, cancellationToken);
            var responseBody = await response.Content.ReadAsStringAsync(cancellationToken);

            var payloadDict = new Dictionary<string, object>
            {
                ["statusCode"] = (double)response.StatusCode,
                ["body"] = responseBody,
                ["isSuccess"] = response.IsSuccessStatusCode
            };
            var payload = JsonSerializer.SerializeToElement(payloadDict);

            if (response.IsSuccessStatusCode)
            {
                return new NodeResult("success", payload, NodeExecutionStatus.Succeeded);
            }
            else
            {
                return new NodeResult("error", payload, NodeExecutionStatus.Failed);
            }
        }
        catch (Exception ex)
        {
            return new NodeResult("error", 
                JsonSerializer.SerializeToElement(new Dictionary<string, object> { ["error"] = ex.Message }), 
                NodeExecutionStatus.Failed);
        }
    }
}
