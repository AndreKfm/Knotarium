using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using KnotGarden.Core.Contracts;
using MailKit;
using MailKit.Net.Imap;
using MailKit.Search;
using MimeKit;

namespace KnotGarden.Features.Nodes;

/// <summary>
/// Fetches recent messages from an IMAP mailbox (MailKit) — a pull node that pairs with the polling
/// trigger. The password is resolved from a stored credential. Per-message summarization is factored
/// into <see cref="SummarizeMessage"/> so it is unit-testable. Emits <c>result = { messages, count }</c>.
/// </summary>
public class ImapFetchNodeTask : INodeTask
{
    private readonly ISecretResolver _secretResolver;

    public ImapFetchNodeTask(ISecretResolver secretResolver) => _secretResolver = secretResolver;

    public async Task<LegacyNodeResult> ExecuteAsync(NodeExecutionContext context, CancellationToken cancellationToken)
    {
        var host = Input(context, "host");
        var username = Input(context, "username");
        if (string.IsNullOrWhiteSpace(host) || string.IsNullOrWhiteSpace(username))
        {
            return new LegacyNodeResult.Failure("Email fetch failed: 'host' and 'username' are required.");
        }

        var port = int.TryParse(Input(context, "port"), out var parsedPort) ? parsedPort : 993;
        var security = (Input(context, "security") ?? "ssl").Trim().ToLowerInvariant();
        var folderName = Input(context, "folder");
        var limit = int.TryParse(Input(context, "limit"), out var parsedLimit) && parsedLimit > 0 ? parsedLimit : 10;
        var unseenOnly = IsTrue(context, "unseenOnly");
        var markSeen = IsTrue(context, "markSeen");

        var credentialRef = Input(context, "credentialRef");
        var password = !string.IsNullOrWhiteSpace(credentialRef)
            ? await _secretResolver.ResolveAsync(credentialRef!, cancellationToken)
            : null;

        try
        {
            using var client = new ImapClient();
            await client.ConnectAsync(host, port, SmtpSendNodeTask.SocketOptionFor(security), cancellationToken);
            await client.AuthenticateAsync(username, password ?? string.Empty, cancellationToken);

            var folder = string.IsNullOrWhiteSpace(folderName)
                ? client.Inbox
                : await client.GetFolderAsync(folderName, cancellationToken);
            await folder.OpenAsync(markSeen ? FolderAccess.ReadWrite : FolderAccess.ReadOnly, cancellationToken);

            var uids = await folder.SearchAsync(unseenOnly ? SearchQuery.NotSeen : SearchQuery.All, cancellationToken);
            var selected = uids.Skip(Math.Max(0, uids.Count - limit)).ToList();

            var messages = new List<Dictionary<string, object?>>(selected.Count);
            foreach (var uid in selected)
            {
                var message = await folder.GetMessageAsync(uid, cancellationToken);
                messages.Add(SummarizeMessage(message, uid.Id));
                if (markSeen)
                {
                    await folder.AddFlagsAsync(uid, MessageFlags.Seen, silent: true, cancellationToken);
                }
            }

            await client.DisconnectAsync(true, cancellationToken);

            return new LegacyNodeResult.Success(new Dictionary<string, object>
            {
                ["result"] = new Dictionary<string, object> { ["messages"] = messages, ["count"] = messages.Count },
            });
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            return new LegacyNodeResult.Failure($"Email fetch failed: {ex.Message}");
        }
    }

    /// <summary>Flattens a fetched message into the JSON-friendly summary emitted downstream.</summary>
    internal static Dictionary<string, object?> SummarizeMessage(MimeMessage message, uint uid) => new()
    {
        ["uid"] = uid,
        ["from"] = message.From.ToString(),
        ["to"] = message.To.ToString(),
        ["subject"] = message.Subject ?? string.Empty,
        ["date"] = message.Date.UtcDateTime.ToString("o"),
        ["body"] = message.TextBody ?? message.HtmlBody ?? string.Empty,
    };

    private static string? Input(NodeExecutionContext context, string key)
        => context.Inputs.TryGetValue(key, out var value) ? value?.ToString() : null;

    private static bool IsTrue(NodeExecutionContext context, string key)
        => context.Inputs.TryGetValue(key, out var value) && value is not null && bool.TryParse(value.ToString(), out var flag) && flag;
}
