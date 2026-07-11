using System;
using System.Collections.Generic;
using System.Linq;
using Knotarium.Features.Nodes;
using MailKit.Security;
using MimeKit;
using Xunit;

namespace Knotarium.Tests.Nodes;

/// <summary>
/// Unit coverage for the testable seams of the email nodes — message construction (SMTP) and message
/// summarization (IMAP). The live connect/send/fetch paths require a real server and are exercised by
/// manual integration, not here.
/// </summary>
public class EmailNodeTaskTests
{
    [Fact]
    public void BuildMessage_parses_recipients_subject_and_text_body()
    {
        var message = SmtpSendNodeTask.BuildMessage(
            from: "sender@example.com",
            to: "a@example.com, b@example.com; c@example.com",
            cc: "cc@example.com",
            subject: "Hi",
            body: "plain text",
            isHtml: false,
            attachments: null);

        Assert.Equal("sender@example.com", ((MailboxAddress)message.From[0]).Address);
        Assert.Equal(3, message.To.Count);
        Assert.Single(message.Cc);
        Assert.Equal("Hi", message.Subject);
        Assert.Equal("plain text", message.TextBody);
        Assert.Null(message.HtmlBody);
    }

    [Fact]
    public void BuildMessage_uses_html_body_when_flagged()
    {
        var message = SmtpSendNodeTask.BuildMessage(
            "s@example.com", "r@example.com", null, "S", "<b>hi</b>", isHtml: true, attachments: null);

        Assert.Equal("<b>hi</b>", message.HtmlBody);
        Assert.Null(message.TextBody);
    }

    [Fact]
    public void BuildMessage_decodes_base64_attachments_from_keyvalue_rows()
    {
        var attachments = new List<Dictionary<string, object>>
        {
            new() { ["name"] = "hello.txt", ["value"] = Convert.ToBase64String("hi"u8.ToArray()) },
        };

        var message = SmtpSendNodeTask.BuildMessage(
            "s@example.com", "r@example.com", null, "S", "body", isHtml: false, attachments: attachments);

        var attachment = message.Attachments.OfType<MimePart>().Single();
        Assert.Equal("hello.txt", attachment.FileName);
    }

    [Theory]
    [InlineData("ssl", SecureSocketOptions.SslOnConnect)]
    [InlineData("starttls", SecureSocketOptions.StartTls)]
    [InlineData("none", SecureSocketOptions.None)]
    [InlineData("auto", SecureSocketOptions.Auto)]
    [InlineData("something-else", SecureSocketOptions.Auto)]
    public void SocketOptionFor_maps_security_names(string security, SecureSocketOptions expected)
    {
        Assert.Equal(expected, SmtpSendNodeTask.SocketOptionFor(security));
    }

    [Fact]
    public void SummarizeMessage_flattens_headers_and_body()
    {
        var message = new MimeMessage();
        message.From.Add(MailboxAddress.Parse("alice@example.com"));
        message.To.Add(MailboxAddress.Parse("bob@example.com"));
        message.Subject = "Report";
        message.Date = new DateTimeOffset(2026, 7, 10, 8, 0, 0, TimeSpan.Zero);
        message.Body = new TextPart("plain") { Text = "the body" };

        var summary = ImapFetchNodeTask.SummarizeMessage(message, uid: 42);

        Assert.Equal(42u, summary["uid"]);
        Assert.Contains("alice@example.com", (string)summary["from"]!);
        Assert.Equal("Report", summary["subject"]);
        Assert.Equal("the body", summary["body"]);
        Assert.Equal("2026-07-10T08:00:00.0000000Z", summary["date"]);
    }
}
