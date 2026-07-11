using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using KnotGarden.Core.Contracts;
using KnotGarden.Core.Domain;

namespace KnotGarden.NodeRuntime;

public class DeclarativeExecutor : INodeExecutor
{
    private readonly NodePackageManifest _manifest;

    public DeclarativeExecutor(NodePackageManifest manifest)
    {
        _manifest = manifest ?? throw new ArgumentNullException(nameof(manifest));
    }

    public async ValueTask<NodeResult> ExecuteAsync(
        NodeInput input,
        INodeContext context,
        CancellationToken cancellationToken)
    {
        string nodeType = _manifest.Id.Value;

        try
        {
            switch (nodeType.ToLowerInvariant())
            {
                case "start":
                case "manualtrigger":
                    return new NodeResult("result", null, NodeExecutionStatus.Succeeded);

                case "switch":
                    {
                        var value = GetStringParam(input, "value", "");
                        var casesStr = GetStringParam(input, "cases", "");
                        var cases = casesStr.Split(new[] { ',', ';', '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
                        
                        string port = "default";
                        foreach (var c in cases)
                        {
                            if (string.Equals(c, value, StringComparison.OrdinalIgnoreCase))
                            {
                                port = c;
                                break;
                            }
                        }
                        
                        var payload = JsonSerializer.SerializeToElement(new Dictionary<string, object>
                        {
                            ["selectedPort"] = port
                        });
                        return new NodeResult(port, payload, NodeExecutionStatus.Succeeded);
                    }

                case "transform":
                    {
                        if (!input.Parameters.TryGetValue("inputJson", out var inputJsonElem))
                        {
                            return new NodeResult("error", 
                                JsonSerializer.SerializeToElement(new Dictionary<string, object> { ["error"] = "Missing required inputJson parameter." }), 
                                NodeExecutionStatus.Failed);
                        }

                        var jsonPath = GetStringParam(input, "jsonPath", "");
                        if (string.IsNullOrEmpty(jsonPath))
                        {
                            return new NodeResult("error", 
                                JsonSerializer.SerializeToElement(new Dictionary<string, object> { ["error"] = "Missing required jsonPath parameter." }), 
                                NodeExecutionStatus.Failed);
                        }

                        // Support raw JSON strings as well as JsonElement
                        JsonElement elementToQuery = inputJsonElem;
                        if (inputJsonElem.ValueKind == JsonValueKind.String)
                        {
                            var rawStr = inputJsonElem.GetString();
                            if (!string.IsNullOrWhiteSpace(rawStr))
                            {
                                try
                                {
                                    using var doc = JsonDocument.Parse(rawStr);
                                    elementToQuery = doc.RootElement.Clone();
                                }
                                catch
                                {
                                    // Treat as string literal
                                }
                            }
                        }

                        var resultElement = ExpressionEvaluator.NavigateJson(elementToQuery, jsonPath);
                        if (resultElement == null)
                        {
                            return new NodeResult("error", 
                                JsonSerializer.SerializeToElement(new Dictionary<string, object> { ["error"] = $"JSONPath '{jsonPath}' resolved to null or was not found." }), 
                                NodeExecutionStatus.Failed);
                        }

                        return new NodeResult("success", resultElement.Value, NodeExecutionStatus.Succeeded);
                    }

                case "end":
                    return new NodeResult("", null, NodeExecutionStatus.Succeeded);

                case "log":
                    {
                        var message = GetStringParam(input, "message", "log message");
                        context.Logger.LogInformation("{Message}", message);
                        
                        var payload = JsonSerializer.SerializeToElement(new Dictionary<string, object>
                        {
                            ["result"] = message
                        });
                        return new NodeResult("result", payload, NodeExecutionStatus.Succeeded);
                    }

                case "setvariable":
                    {
                        var varName = GetStringParam(input, "name", "");
                        if (string.IsNullOrEmpty(varName))
                        {
                            varName = GetStringParam(input, "variableName", "");
                        }

                        input.Parameters.TryGetValue("value", out var valueElem);
                        object? val = ConvertJsonElement(valueElem);

                        if (!string.IsNullOrEmpty(varName))
                        {
                            context.State.SetVariable(varName, val);
                        }

                        return new NodeResult("result", null, NodeExecutionStatus.Succeeded);
                    }

                case "delay":
                    {
                        int durationMs = 0;
                        if (input.Parameters.TryGetValue("delayMs", out var msElem))
                        {
                            if (msElem.ValueKind == JsonValueKind.Number)
                            {
                                durationMs = (int)msElem.GetDouble();
                            }
                            else if (int.TryParse(msElem.GetString(), out var parsedMs))
                            {
                                durationMs = parsedMs;
                            }
                        }
                        else if (input.Parameters.TryGetValue("duration", out var durElem))
                        {
                            var durStr = durElem.GetString();
                            if (TimeSpan.TryParse(durStr, System.Globalization.CultureInfo.InvariantCulture, out var ts))
                            {
                                durationMs = (int)ts.TotalMilliseconds;
                            }
                        }

                        if (durationMs > 0)
                        {
                            await Task.Delay(durationMs, cancellationToken);
                        }

                        return new NodeResult("result", null, NodeExecutionStatus.Succeeded);
                    }

                case "condition":
                    {
                        input.Parameters.TryGetValue("left", out var leftElem);
                        input.Parameters.TryGetValue("right", out var rightElem);
                        var opStr = GetStringParam(input, "operator", "Equal");

                        var left = ConvertJsonElement(leftElem);
                        var right = ConvertJsonElement(rightElem);

                        bool evaluation = EvaluateCondition(left, right, opStr);
                        string port = evaluation ? "true" : "false";

                        var payload = JsonSerializer.SerializeToElement(new Dictionary<string, object>
                        {
                            ["selectedPort"] = port
                        });

                        return new NodeResult(port, payload, NodeExecutionStatus.Succeeded);
                    }

                case "httprequest":
                    {
                        if (context.Http == null)
                        {
                            return new NodeResult("error", 
                                JsonSerializer.SerializeToElement(new Dictionary<string, object> { ["error"] = "HTTP capability not granted or client missing." }), 
                                NodeExecutionStatus.Failed);
                        }

                        var url = GetStringParam(input, "url", "");
                        if (string.IsNullOrEmpty(url))
                        {
                            return new NodeResult("error", 
                                JsonSerializer.SerializeToElement(new Dictionary<string, object> { ["error"] = "Missing required url parameter." }), 
                                NodeExecutionStatus.Failed);
                        }

                        var methodStr = GetStringParam(input, "method", "GET");
                        var bodyStr = GetStringParam(input, "body", "");
                        var headersStr = GetStringParam(input, "headers", "");

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

                case "forloop":
                    {
                        var mode = GetStringParam(input, "mode", "foreach");
                        var payload = JsonSerializer.SerializeToElement(new Dictionary<string, object>
                        {
                            ["selectedPort"] = "success",
                            ["results"] = Array.Empty<object>()
                        });
                        return new NodeResult("success", payload, NodeExecutionStatus.Succeeded);
                    }

                default:
                    return new NodeResult("result", null, NodeExecutionStatus.Succeeded);
            }
        }
        catch (OperationCanceledException)
        {
            return new NodeResult("error", 
                JsonSerializer.SerializeToElement(new Dictionary<string, object> { ["error"] = "Execution was cancelled." }), 
                NodeExecutionStatus.Cancelled);
        }
        catch (Exception ex)
        {
            return new NodeResult("error", 
                JsonSerializer.SerializeToElement(new Dictionary<string, object> { ["error"] = ex.Message }), 
                NodeExecutionStatus.Failed);
        }
    }

    private static string GetStringParam(NodeInput input, string key, string defaultVal)
    {
        if (input.Parameters.TryGetValue(key, out var elem))
        {
            if (elem.ValueKind == JsonValueKind.String)
                return elem.GetString() ?? defaultVal;
            return elem.GetRawText() ?? defaultVal;
        }
        return defaultVal;
    }

    private static object? ConvertJsonElement(JsonElement? element)
    {
        if (element == null) return null;
        var el = element.Value;
        return el.ValueKind switch
        {
            JsonValueKind.String => el.GetString(),
            JsonValueKind.Number => el.GetDouble(),
            JsonValueKind.True => true,
            JsonValueKind.False => false,
            JsonValueKind.Null => null,
            _ => el
        };
    }

    private static bool EvaluateCondition(object? left, object? right, string opStr)
    {
        if (left == null || right == null)
        {
            return opStr.Equals("Equal", StringComparison.OrdinalIgnoreCase) ? left == right : left != right;
        }

        if (left.GetType() != right.GetType())
        {
            return false;
        }

        if (left is double lNum && right is double rNum)
        {
            return opStr.ToLowerInvariant() switch
            {
                "equal" => Math.Abs(lNum - rNum) < double.Epsilon,
                "notequal" => Math.Abs(lNum - rNum) >= double.Epsilon,
                "greaterthan" => lNum > rNum,
                "lessthan" => lNum < rNum,
                "greaterthanorequal" => lNum >= rNum,
                "lessthanorequal" => lNum <= rNum,
                _ => false
            };
        }

        if (left is string lStr && right is string rStr)
        {
            return opStr.ToLowerInvariant() switch
            {
                "equal" => string.Equals(lStr, rStr, StringComparison.Ordinal),
                "notequal" => !string.Equals(lStr, rStr, StringComparison.Ordinal),
                "contains" => lStr.Contains(rStr, StringComparison.Ordinal),
                "greaterthan" => string.Compare(lStr, rStr, StringComparison.Ordinal) > 0,
                "lessthan" => string.Compare(lStr, rStr, StringComparison.Ordinal) < 0,
                "greaterthanorequal" => string.Compare(lStr, rStr, StringComparison.Ordinal) >= 0,
                "lessthanorequal" => string.Compare(lStr, rStr, StringComparison.Ordinal) <= 0,
                _ => false
            };
        }

        if (left is bool lBool && right is bool rBool)
        {
            return opStr.ToLowerInvariant() switch
            {
                "equal" => lBool == rBool,
                "notequal" => lBool != rBool,
                _ => false
            };
        }

        return false;
    }
}
