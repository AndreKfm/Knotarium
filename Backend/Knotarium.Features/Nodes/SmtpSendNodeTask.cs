using System;
using System.Collections;
using System.Collections.Generic;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Knotarium.Core.Contracts;
using MailKit.Net.Smtp;
using MailKit.Security;
using MimeKit;

namespace Knotarium.Features.Nodes;

/// <summary>
/// Composes and sends an email over SMTP (MailKit). The password is resolved from a stored credential;
/// attachments are supplied as base64. The message construction is factored into <see cref="BuildMessage"/>
/// so it is unit-testable without a live server. Emits <c>result = { messageId, sent }</c>.
/// </summary>
public class SmtpSendNodeTask : INodeTask
{
    private readonly ISecretResolver _secretResolver;

    public SmtpSendNodeTask(ISecretResolver secretResolver) => _secretResolver = secretResolver;

    public async Task<LegacyNodeResult> ExecuteAsync(NodeExecutionContext context, CancellationToken cancellationToken)
    {
        var host = Input(context, "host");
        if (string.IsNullOrWhiteSpace(host))
        {
            return new LegacyNodeResult.Failure("Email send failed: missing required 'host'.");
        }
        var from = Input(context, "from");
        var to = Input(context, "to");
        if (string.IsNullOrWhiteSpace(from) || string.IsNullOrWhiteSpace(to))
        {
            return new LegacyNodeResult.Failure("Email send failed: 'from' and 'to' are required.");
        }

        var port = int.TryParse(Input(context, "port"), out var parsedPort) ? parsedPort : 587;
        var security = (Input(context, "security") ?? "auto").Trim().ToLowerInvariant();
        var username = Input(context, "username");
        var credentialRef = Input(context, "credentialRef");
        var password = !string.IsNullOrWhiteSpace(credentialRef)
            ? await _secretResolver.ResolveAsync(credentialRef!, cancellationToken)
            : null;

        MimeMessage message;
        try
        {
            message = BuildMessage(
                from,
                to,
                Input(context, "cc"),
                Input(context, "subject") ?? string.Empty,
                Input(context, "body") ?? string.Empty,
                IsTrue(context, "isHtml"),
                context.Inputs.TryGetValue("attachments", out var attachments) ? attachments : null);
        }
        catch (Exception ex)
        {
            return new LegacyNodeResult.Failure($"Email send failed: {ex.Message}");
        }

        try
        {
            using var client = new SmtpClient();
            await client.ConnectAsync(host, port, SocketOptionFor(security), cancellationToken);
            if (!string.IsNullOrEmpty(username))
            {
                await client.AuthenticateAsync(username, password ?? string.Empty, cancellationToken);
            }
            await client.SendAsync(message, cancellationToken);
            await client.DisconnectAsync(true, cancellationToken);

            return new LegacyNodeResult.Success(new Dictionary<string, object>
            {
                ["result"] = new Dictionary<string, object> { ["messageId"] = message.MessageId, ["sent"] = true },
            });
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            return new LegacyNodeResult.Failure($"Email send failed: {ex.Message}");
        }
    }

    internal static SecureSocketOptions SocketOptionFor(string security) => security switch
    {
        "ssl" => SecureSocketOptions.SslOnConnect,
        "starttls" => SecureSocketOptions.StartTls,
        "none" => SecureSocketOptions.None,
        _ => SecureSocketOptions.Auto,
    };

    /// <summary>Builds the MIME message from the node's fields. Recipients accept comma/semicolon/newline
    /// separated lists; attachments are a name→base64 map (keyValue rows or a JSON object).</summary>
    internal static MimeMessage BuildMessage(string from, string to, string? cc, string subject, string body, bool isHtml, object? attachments)
    {
        var message = new MimeMessage();
        message.From.Add(MailboxAddress.Parse(from));
        foreach (var address in SplitAddresses(to))
        {
            message.To.Add(MailboxAddress.Parse(address));
        }
        foreach (var address in SplitAddresses(cc))
        {
            message.Cc.Add(MailboxAddress.Parse(address));
        }
        message.Subject = subject;

        var builder = new BodyBuilder();
        if (isHtml)
        {
            builder.HtmlBody = body;
        }
        else
        {
            builder.TextBody = body;
        }

        foreach (var (name, base64) in EnumerateAttachments(attachments))
        {
            builder.Attachments.Add(name, Convert.FromBase64String(base64));
        }

        message.Body = builder.ToMessageBody();
        return message;
    }

    private static IEnumerable<string> SplitAddresses(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
        {
            yield break;
        }
        foreach (var part in raw.Split(new[] { ',', ';', '\n', '\r' }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            yield return part;
        }
    }

    private static IEnumerable<(string Name, string Base64)> EnumerateAttachments(object? raw)
    {
        switch (raw)
        {
            case JsonElement element when element.ValueKind == JsonValueKind.Object:
                foreach (var property in element.EnumerateObject())
                {
                    if (property.Value.ValueKind == JsonValueKind.String)
                    {
                        yield return (property.Name, property.Value.GetString()!);
                    }
                }
                break;
            case JsonElement array when array.ValueKind == JsonValueKind.Array:
                foreach (var item in array.EnumerateArray())
                {
                    if (item.ValueKind == JsonValueKind.Object
                        && item.TryGetProperty("name", out var n) && n.ValueKind == JsonValueKind.String
                        && item.TryGetProperty("value", out var v) && v.ValueKind == JsonValueKind.String)
                    {
                        yield return (n.GetString()!, v.GetString()!);
                    }
                }
                break;
            case IEnumerable enumerable and not string:
                foreach (var item in enumerable)
                {
                    if (item is IDictionary<string, object> row
                        && row.TryGetValue("name", out var name) && name is string nameString
                        && row.TryGetValue("value", out var value) && value is string valueString)
                    {
                        yield return (nameString, valueString);
                    }
                }
                break;
        }
    }

    private static string? Input(NodeExecutionContext context, string key)
        => context.Inputs.TryGetValue(key, out var value) ? value?.ToString() : null;

    private static bool IsTrue(NodeExecutionContext context, string key)
        => context.Inputs.TryGetValue(key, out var value) && value is not null && bool.TryParse(value.ToString(), out var flag) && flag;
}
