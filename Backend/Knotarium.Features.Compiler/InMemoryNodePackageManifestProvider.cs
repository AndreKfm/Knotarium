using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Threading;
using System.Threading.Tasks;
using Knotarium.Core.Contracts;
using Knotarium.Core.Domain;

namespace Knotarium.Features.Compiler;

public class InMemoryNodePackageManifestProvider : INodePackageManifestProvider
{
    private readonly Dictionary<NodePackageId, NodePackageManifest> _manifests = new();

    public InMemoryNodePackageManifestProvider()
    {
        // 1. Start node manifest
        Register(new NodePackageManifest(
            new NodePackageId("start"),
            "1.0.0",
            "Start",
            "Triggers",
            NodeTier.Declarative,
            NodeSideEffectKind.IdempotentSideEffect,
            RecoveryMode.FailImmediately,
            0,
            new List<string>(),
            new List<ParameterDefinition>(),
            new List<OutputDefinition> { new("result") },
            triggerOnly: true
        ));

        // 2. Log node manifest
        Register(new NodePackageManifest(
            new NodePackageId("log"),
            "1.0.0",
            "Log",
            "Utility",
            NodeTier.Declarative,
            NodeSideEffectKind.IdempotentSideEffect,
            RecoveryMode.FailImmediately,
            10,
            new List<string>(),
            new List<ParameterDefinition> { new("message", "string", false, true) },
            new List<OutputDefinition> { new("result") }
        ));

        // 3. End node manifest
        Register(new NodePackageManifest(
            new NodePackageId("end"),
            "1.0.0",
            "End",
            "Triggers",
            NodeTier.Declarative,
            NodeSideEffectKind.IdempotentSideEffect,
            RecoveryMode.FailImmediately,
            0,
            new List<string>(),
            new List<ParameterDefinition> { new("payload", "string", false, true) },
            new List<OutputDefinition>()
        ));

        // 4. Condition node manifest
        Register(new NodePackageManifest(
            new NodePackageId("condition"),
            "1.0.0",
            "Condition",
            "Control",
            NodeTier.Declarative,
            NodeSideEffectKind.IdempotentSideEffect,
            RecoveryMode.FailImmediately,
            10,
            new List<string>(),
            new List<ParameterDefinition>
            {
                // New typed logic graph (D3/B5). Expression:false ⇒ the executor hands it over
                // unresolved so the task resolves its own refs with found-ness (D7). Hidden from the
                // generic ManifestForm (the FE renders a summary + "Edit logic" — Phase 4).
                new("logic", "json", false, false),
                // Legacy operands — retained so unopened legacy nodes still run (precedence: logic first).
                new("left", "string", false, true),
                new("operator", "string", false, false),
                new("right", "string", false, true)
            },
            new List<OutputDefinition> { new("true"), new("false") }
        ));

        // 5. Delay node manifest
        Register(new NodePackageManifest(
            new NodePackageId("delay"),
            "1.0.0",
            "Delay",
            "Control",
            NodeTier.Declarative,
            NodeSideEffectKind.IdempotentSideEffect,
            RecoveryMode.FailImmediately,
            60,
            new List<string>(),
            new List<ParameterDefinition> { new("delayMs", "number", false, true), new("duration", "string", false, true) },
            new List<OutputDefinition> { new("result") }
        ));

        // 6. HttpRequest node manifest
        Register(new NodePackageManifest(
            new NodePackageId("httpRequest"),
            "1.0.0",
            "HTTP Request",
            "Integrations",
            NodeTier.Declarative,
            NodeSideEffectKind.NonIdempotentSideEffect,
            RecoveryMode.RetryAutomatically,
            30,
            new List<string> { "network" },
            new List<ParameterDefinition>
            {
                new("url", "string", false, true),
                new("method", "string", false, false),
                new("body", "string", false, true),
                new("headers", "string", false, true),
                // Flexible authentication. authType picks the scheme; the actual secret is pulled at run
                // time from the referenced credential (never stored in the workflow). username/headerName/
                // valuePrefix are only meaningful for basic / api-key schemes.
                new("authType", "enum", false, false, new List<string> { "none", "bearer", "basic", "apiKey" }),
                new("authCredentialRef", "credentialRef", false, false),
                new("authUsername", "string", false, true),
                new("authHeaderName", "string", false, false),
                new("authValuePrefix", "string", false, false)
            },
            new List<OutputDefinition> { new("success", "object"), new("error", "string") }
        ));

        // 6b. Database Query node — parameterized SQL against Postgres/SQLite (Phase 2 protocol node).
        Register(new NodePackageManifest(
            new NodePackageId("dbQuery"),
            "1.0.0",
            "Database Query",
            "Integrations",
            NodeTier.Declarative,
            NodeSideEffectKind.NonIdempotentSideEffect,
            RecoveryMode.RetryAutomatically,
            30,
            new List<string> { NodeCapabilities.Network, NodeCapabilities.Database },
            new List<ParameterDefinition>
            {
                new("provider", "enum", true, false, new List<string> { "postgres", "sqlite" }),
                // The connection string is stored as a secret credential and resolved at run time.
                new("connectionRef", "credentialRef", true, false),
                new("query", "string", true, true,
                    Description: "SQL to run. Bind values with @name and supply them under Parameters — never string-concatenate."),
                new("parameters", "keyValue", false, true,
                    Description: "Named query parameters (bound as @name); injection-safe."),
            },
            new List<OutputDefinition>
            {
                new("result", "object", new List<FieldSchema> { new("rows", "array"), new("rowCount", "number") }),
            }
        ));

        // 6c. File Read node — read a local file as text or base64 (Phase 2 protocol node).
        Register(new NodePackageManifest(
            new NodePackageId("fileRead"),
            "1.0.0",
            "File Read",
            "Integrations",
            NodeTier.Declarative,
            NodeSideEffectKind.IdempotentSideEffect,
            RecoveryMode.RetryAutomatically,
            30,
            new List<string> { NodeCapabilities.FilesystemRead },
            new List<ParameterDefinition>
            {
                new("path", "string", true, true, Description: "Absolute path to the file to read (must be within a permitted directory)."),
                new("encoding", "enum", false, false, new List<string> { "utf8", "base64" }),
            },
            new List<OutputDefinition>
            {
                new("result", "object", new List<FieldSchema>
                {
                    new("content", "string"), new("encoding", "string"), new("size", "number"), new("path", "string"),
                }),
            }
        ));

        // 6d. File Write node — write text or base64 bytes to a local file (Phase 2 protocol node).
        Register(new NodePackageManifest(
            new NodePackageId("fileWrite"),
            "1.0.0",
            "File Write",
            "Integrations",
            NodeTier.Declarative,
            NodeSideEffectKind.NonIdempotentSideEffect,
            RecoveryMode.FailImmediately,
            30,
            new List<string> { NodeCapabilities.FilesystemWrite },
            new List<ParameterDefinition>
            {
                new("path", "string", true, true, Description: "Destination path (must be within a permitted directory); missing parent folders are created."),
                new("content", "string", false, true),
                new("encoding", "enum", false, false, new List<string> { "utf8", "base64" }),
                new("append", "boolean", false, false),
            },
            new List<OutputDefinition>
            {
                new("result", "object", new List<FieldSchema> { new("path", "string"), new("bytesWritten", "number") }),
            }
        ));

        // 6e. Email Send (SMTP) node — compose + send mail (Phase 2 protocol node).
        Register(new NodePackageManifest(
            new NodePackageId("smtpSend"),
            "1.0.0",
            "Email Send (SMTP)",
            "Integrations",
            NodeTier.Declarative,
            NodeSideEffectKind.NonIdempotentSideEffect,
            RecoveryMode.RetryAutomatically,
            60,
            new List<string> { "network" },
            new List<ParameterDefinition>
            {
                new("host", "string", true, false),
                new("port", "number", false, false),
                new("security", "enum", false, false, new List<string> { "auto", "starttls", "ssl", "none" }),
                new("username", "string", false, false),
                new("credentialRef", "credentialRef", false, false),
                new("from", "string", true, true),
                new("to", "string", true, true, Description: "Recipients — comma/semicolon/newline separated."),
                new("cc", "string", false, true),
                new("subject", "string", false, true),
                new("body", "string", false, true),
                new("isHtml", "boolean", false, false),
                new("attachments", "keyValue", false, true, Description: "filename → base64 content."),
            },
            new List<OutputDefinition>
            {
                new("result", "object", new List<FieldSchema> { new("messageId", "string"), new("sent", "boolean") }),
            }
        ));

        // 6f. Email Fetch (IMAP) node — pull recent messages; pairs with the polling trigger (Phase 2).
        Register(new NodePackageManifest(
            new NodePackageId("imapFetch"),
            "1.0.0",
            "Email Fetch (IMAP)",
            "Integrations",
            NodeTier.Declarative,
            NodeSideEffectKind.IdempotentSideEffect,
            RecoveryMode.RetryAutomatically,
            60,
            new List<string> { "network" },
            new List<ParameterDefinition>
            {
                new("host", "string", true, false),
                new("port", "number", false, false),
                new("security", "enum", false, false, new List<string> { "ssl", "starttls", "auto", "none" }),
                new("username", "string", true, false),
                new("credentialRef", "credentialRef", false, false),
                new("folder", "string", false, false, Description: "Mailbox folder; defaults to INBOX."),
                new("limit", "number", false, false),
                new("unseenOnly", "boolean", false, false),
                new("markSeen", "boolean", false, false),
            },
            new List<OutputDefinition>
            {
                new("result", "object", new List<FieldSchema> { new("messages", "array"), new("count", "number") }),
            }
        ));

        // 6g. MQTT Publish node — publish a message to a topic (Phase 2 protocol node). The push
        // consumer/trigger is a separate long-lived subsystem, delivered on its own.
        Register(new NodePackageManifest(
            new NodePackageId("mqPublish"),
            "1.0.0",
            "MQTT Publish",
            "Integrations",
            NodeTier.Declarative,
            NodeSideEffectKind.NonIdempotentSideEffect,
            RecoveryMode.RetryAutomatically,
            30,
            new List<string> { "network" },
            new List<ParameterDefinition>
            {
                new("host", "string", true, false),
                new("port", "number", false, false),
                new("clientId", "string", false, false),
                new("username", "string", false, false),
                new("credentialRef", "credentialRef", false, false),
                new("useTls", "boolean", false, false),
                new("topic", "string", true, true),
                new("payload", "string", false, true),
                new("qos", "enum", false, false, new List<string> { "0", "1", "2" }),
                new("retain", "boolean", false, false),
            },
            new List<OutputDefinition>
            {
                new("result", "object", new List<FieldSchema> { new("topic", "string"), new("published", "boolean") }),
            }
        ));

        // 6h. AI Prompt node — one LLM call over the incoming data (classify / extract / summarize /
        // draft). Uses the instance-wide AI provider config; optional per-node model/token overrides.
        // Idempotent: the call has no external side effect, so automatic retry after a transport
        // failure is safe (the reply may differ, which a workflow using AI must tolerate anyway).
        Register(new NodePackageManifest(
            new NodePackageId("aiPrompt"),
            "1.0.0",
            "AI Prompt",
            "AI",
            NodeTier.Declarative,
            NodeSideEffectKind.IdempotentSideEffect,
            RecoveryMode.RetryAutomatically,
            300,
            new List<string> { "network" },
            new List<ParameterDefinition>
            {
                new("prompt", "string", true, true, Description: "The task for the model. Reference upstream data with {{ }}."),
                new("systemPrompt", "string", false, true, Description: "Optional role/instructions; a safe default is applied when empty."),
                new("jsonSchema", "string", false, false, Description: "Optional JSON schema; when set, the node emits a parsed object conforming to it instead of raw text."),
                new("model", "string", false, false, Description: "Override the configured model for this node only."),
                new("maxTokens", "number", false, false, Description: "Override the configured completion token cap for this node only."),
            },
            new List<OutputDefinition>
            {
                new("result", "any"),
            }
        ));

        // 7. SetVariable node manifest
        Register(new NodePackageManifest(
            new NodePackageId("setVariable"),
            "1.0.0",
            "Set Variable",
            "Utility",
            NodeTier.Declarative,
            NodeSideEffectKind.IdempotentSideEffect,
            RecoveryMode.FailImmediately,
            10,
            new List<string>(),
            new List<ParameterDefinition>
            {
                // The variable to write. The whole system (runtime, compiler, node display)
                // reads this under "variableName"; the form field must persist the same key.
                // Supports keyed/nested paths: myDict["name"], list[0], config.servers[2].host.
                new("variableName", "string", false, false,
                    Description: "Variable name, or a nested path: myDict[\"name\"], list[0]"),
                new("value", "string", false, true)
            },
            new List<OutputDefinition> { new("result") }
        ));

        // 7b. Set Variables node manifest (bulk-initialize several globals at once)
        Register(new NodePackageManifest(
            new NodePackageId("setVariables"),
            "1.0.0",
            "Set Variables",
            "Utility",
            NodeTier.Compiled,
            NodeSideEffectKind.IdempotentSideEffect,
            RecoveryMode.FailImmediately,
            10,
            new List<string>(),
            new List<ParameterDefinition>
            {
                // Rows of { name, value }; values are expression-enabled (evaluated by the executor).
                new("variables", "keyValue", false, true)
            },
            new List<OutputDefinition> { new("result") }
        ));

        // 8. Subflow node manifest
        Register(new NodePackageManifest(
            new NodePackageId("subflow"),
            "1.0.0",
            "Subflow",
            "Control",
            NodeTier.Declarative,
            NodeSideEffectKind.IdempotentSideEffect,
            RecoveryMode.FailImmediately,
            300,
            new List<string>(),
            new List<ParameterDefinition> { new("subflowId", "string", false, false) },
            new List<OutputDefinition> { new("result") }
        ));

        // 9. Scheduler node manifest
        Register(new NodePackageManifest(
            new NodePackageId("scheduler"),
            "1.0.0",
            "Cron Scheduler",
            "Triggers",
            NodeTier.Declarative,
            NodeSideEffectKind.IdempotentSideEffect,
            RecoveryMode.FailImmediately,
            0,
            new List<string>(),
            new List<ParameterDefinition>
            {
                new("cronExpression", "string", true, false),
                new("timezoneId", "string", true, false)
            },
            new List<OutputDefinition> { new("triggeredAt", "string") },
            triggerOnly: true
        ));

        // 10. Manual Trigger node manifest
        Register(new NodePackageManifest(
            new NodePackageId("manualTrigger"),
            "1.0.0",
            "Manual Trigger",
            "Triggers",
            NodeTier.Declarative,
            NodeSideEffectKind.IdempotentSideEffect,
            RecoveryMode.FailImmediately,
            0,
            new List<string>(),
            new List<ParameterDefinition>(),
            new List<OutputDefinition> { new("result") },
            triggerOnly: true
        ));

        // 11. Webhook Trigger node manifest
        Register(new NodePackageManifest(
            new NodePackageId("webhookTrigger"),
            "1.0.0",
            "Webhook Trigger",
            "Triggers",
            NodeTier.Compiled,
            NodeSideEffectKind.IdempotentSideEffect,
            RecoveryMode.FailImmediately,
            0,
            new List<string>(),
            new List<ParameterDefinition>
            {
                new("payload", "string", false, false)
            },
            new List<OutputDefinition> { new("result", "object") },
            triggerOnly: true
        ));

        // 12. Switch node manifest
        Register(new NodePackageManifest(
            new NodePackageId("switch"),
            "1.0.0",
            "Switch",
            "Control",
            NodeTier.Declarative,
            NodeSideEffectKind.IdempotentSideEffect,
            RecoveryMode.FailImmediately,
            10,
            new List<string>(),
            new List<ParameterDefinition>
            {
                new("value", "string", true, true),
                new("cases", "string", true, false)
            },
            new List<OutputDefinition> { new("default") }
        ));

        // 13. Transform node manifest
        Register(new NodePackageManifest(
            new NodePackageId("transform"),
            "1.0.0",
            "Transform",
            "Data",
            NodeTier.Declarative,
            NodeSideEffectKind.IdempotentSideEffect,
            RecoveryMode.FailImmediately,
            10,
            new List<string>(),
            new List<ParameterDefinition>
            {
                new("inputJson", "string", true, true),
                new("jsonPath", "string", true, false)
            },
            new List<OutputDefinition> { new("success", "object"), new("error", "string") }
        ));

        // 14. Merge node manifest
        Register(new NodePackageManifest(
            new NodePackageId("merge"),
            "1.0.0",
            "Merge",
            "Data",
            NodeTier.Compiled,
            NodeSideEffectKind.IdempotentSideEffect,
            RecoveryMode.FailImmediately,
            10,
            new List<string>(),
            new List<ParameterDefinition>
            {
                new("array1", "string", false, true),
                new("array2", "string", false, true)
            },
            new List<OutputDefinition> { new("success", "array"), new("error", "string") }
        ));

        // 15. For Loop node manifest
        Register(new NodePackageManifest(
            new NodePackageId("forLoop"),
            "1.0.0",
            "For Loop",
            "Control",
            NodeTier.Declarative,
            NodeSideEffectKind.IdempotentSideEffect,
            RecoveryMode.FailImmediately,
            60,
            new List<string>(),
            new List<ParameterDefinition>
            {
                new("mode", "enum", true, false, new List<string> { "count", "foreach", "while", "batch" }),
                new("collection", "string", false, true),
                new("count", "number", false, true),
                new("condition", "string", false, true),
                new("batchSize", "number", false, true),
                new("end", "string", false, true)
            },
            new List<OutputDefinition> { new("start"), new("success") }
        ));

        // 16b. Parallel For-Each node manifest (concurrent fan-out map over a collection)
        Register(new NodePackageManifest(
            new NodePackageId("parallelForEach"),
            "1.0.0",
            "Parallel For-Each",
            "Control",
            NodeTier.Compiled,
            NodeSideEffectKind.IdempotentSideEffect,
            RecoveryMode.FailImmediately,
            300,
            new List<string>(),
            new List<ParameterDefinition>
            {
                new("collection", "string", false, true),
                new("maxParallelism", "number", false, true),
                new("continueOnError", "boolean", false, true),
                new("end", "string", false, true)
            },
            new List<OutputDefinition> { new("start"), new("success", "array"), new("error", "string") }
        ));

        // 16c. Join node manifest (fan-in: wait for all incoming branches, then aggregate)
        Register(new NodePackageManifest(
            new NodePackageId("join"),
            "1.0.0",
            "Join",
            "Control",
            NodeTier.Compiled,
            NodeSideEffectKind.IdempotentSideEffect,
            RecoveryMode.FailImmediately,
            10,
            new List<string>(),
            new List<ParameterDefinition>(),
            new List<OutputDefinition> { new("result", "array"), new("results", "array") }
        ));

        // 17. Send Notification node manifest
        Register(new NodePackageManifest(
            new NodePackageId("sendNotification"),
            "1.0.0",
            "Send Notification",
            "Integrations",
            NodeTier.Compiled,
            NodeSideEffectKind.NonIdempotentSideEffect,
            RecoveryMode.FailImmediately,
            30,
            new List<string>(),
            new List<ParameterDefinition>
            {
                new("channelId", "notificationChannelRef", true, false),
                new("subject", "string", false, true),
                new("message", "string", true, true)
            },
            new List<OutputDefinition> { new("result") }
        ));

        // 18. Inline Code node manifest (write a short C# script directly in the workflow)
        Register(new NodePackageManifest(
            new NodePackageId("inlineCode"),
            "1.0.0",
            "Inline Code",
            "Scripting",
            NodeTier.Compiled,
            NodeSideEffectKind.IdempotentSideEffect,
            RecoveryMode.FailImmediately,
            InlineCodeTimeoutSeconds,
            new List<string> { NodeCapabilities.CodeExecution },
            new List<ParameterDefinition>
            {
                new("language", "enum", false, false, new List<string> { "csharp" }),
                // expression:false on purpose — the script reaches state via Input.Get / context,
                // and the placeholder evaluator must NOT rewrite "{{ }}" inside source code.
                new("code", "code", true, false)
            },
            new List<OutputDefinition> { new("result"), new("error") }
        ));

        // 19. Polling Trigger node manifest
        Register(new NodePackageManifest(
            new NodePackageId("pollingTrigger"),
            "1.0.0",
            "Polling Trigger",
            "Triggers",
            NodeTier.Declarative,
            NodeSideEffectKind.IdempotentSideEffect,
            RecoveryMode.FailImmediately,
            0,
            new List<string>(),
            new List<ParameterDefinition>
            {
                new("intervalSeconds", "number", true, false),
                new("sourceKind", "enum", true, false, new List<string> { "http", "openapi" }),
                new("changeDetection", "enum", true, false, new List<string> { "etag", "last-modified", "hash", "json-cursor", "always" }),
                new("jsonCursorPath", "string", false, false),
                new("url", "string", false, true),
                new("method", "string", false, false),
                new("headersJson", "string", false, false),
                new("apiKeySecretRef", "string", false, false),
                new("serverConfigId", "string", false, false),
                new("operationId", "string", false, false),
                new("specVersion", "string", false, false)
            },
            new List<OutputDefinition> { new("result") },
            triggerOnly: true
        ));

        // 19b. Error Trigger node manifest — entry point of a global error-handler workflow.
        // Started (origin "error") whenever any other workflow fails; emits the failure context
        // payload on its single "result" port. No config: routing is a global setting.
        Register(new NodePackageManifest(
            new NodePackageId("errorTrigger"),
            "1.0.0",
            "Error Trigger",
            "Triggers",
            NodeTier.Declarative,
            NodeSideEffectKind.IdempotentSideEffect,
            RecoveryMode.FailImmediately,
            0,
            new List<string>(),
            new List<ParameterDefinition>(),
            new List<OutputDefinition> { new("result") },
            triggerOnly: true
        ));

        // 20. Resource Picker node — choose a value from a live resource list at design time and
        // emit it as outputs (value/label) so it can be promoted to a variable and reused read-only.
        Register(new NodePackageManifest(
            new NodePackageId("resourcePicker"),
            "1.0.0",
            "Resource Picker",
            "Data",
            NodeTier.Compiled,
            NodeSideEffectKind.IdempotentSideEffect,
            RecoveryMode.RetryAutomatically,
            30,
            new List<string> { "network", "credentials" },
            new List<ParameterDefinition>
            {
                new("serverConfigId", "string", false, false),
                new("path", "string", false, false),
                new("labelField", "string", false, false),
                new("valueField", "string", false, false),
                new("selection", "resourceLocator", false, false),
            },
            new List<OutputDefinition> { new("result", "object") }
        ));

        // 21. Sticky Note — an editor-only annotation. It carries no execution semantics:
        // no ports, no parameters, never reached by the executor (it has no incoming edges).
        // Registered here only so the compiler recognizes the type and the note round-trips
        // through save/version/restore like any other node (position/text live in _metadata + properties).
        Register(new NodePackageManifest(
            new NodePackageId("stickyNote"),
            "1.0.0",
            "Sticky Note",
            "Annotations",
            NodeTier.Declarative,
            NodeSideEffectKind.IdempotentSideEffect,
            RecoveryMode.FailImmediately,
            0,
            new List<string>(),
            new List<ParameterDefinition>(),
            new List<OutputDefinition>()
        ));

        // 22. Group — an editor-only visual container. Like the sticky note it is inert: it has no
        // ports and is never executed. Membership (which nodes sit inside it) is purely visual,
        // carried by each child's _metadata.parentId, so the backend treats grouped children as
        // ordinary top-level nodes.
        Register(new NodePackageManifest(
            new NodePackageId("group"),
            "1.0.0",
            "Group",
            "Annotations",
            NodeTier.Declarative,
            NodeSideEffectKind.IdempotentSideEffect,
            RecoveryMode.FailImmediately,
            0,
            new List<string>(),
            new List<ParameterDefinition>(),
            new List<OutputDefinition>()
        ));

        // 23. External Device — the generic, config-driven device block (branded by its private runtime
        // manifest). Its connectable pins (evt:<type> outputs, act:<type> inputs) are DYNAMIC, generated
        // from config rather than declared here, so it lists none; the control-flow compiler skips socket
        // validation for it and the reactive layer validates the pin wiring instead.
        Register(new NodePackageManifest(
            new NodePackageId("externalDevice"),
            "1.0.0",
            "External Device",
            "External",
            NodeTier.Declarative,
            NodeSideEffectKind.IdempotentSideEffect,
            RecoveryMode.FailImmediately,
            0,
            new List<string>(),
            new List<ParameterDefinition>
            {
                // Cascaded resource-locator pickers (generic loader names; a provider plugin supplies the
                // values + branding at runtime). The pins are multi-select; child pickers depend on targetId.
                new("targetId", "resourceLocator", true, false,
                    OptionsLoader: "reactor.targets", AllowManualEntry: true,
                    Description: "Which configured device / instance this block represents."),
                new("eventPins", "resourceLocator", false, false,
                    OptionsLoader: "reactor.events", DependsOn: new List<string> { "targetId" },
                    AllowManualEntry: true, Multiple: true,
                    Description: "Events raised by the device to react to (output pins)."),
                new("actionPins", "resourceLocator", false, false,
                    OptionsLoader: "reactor.actions", DependsOn: new List<string> { "targetId" },
                    AllowManualEntry: true, Multiple: true,
                    Description: "Incoming actions raised by the device to react to (output pins)."),
            },
            new List<OutputDefinition>()
        ));
    }

    // Keep the manifest timeout in sync with the executor's enforced timeout (shared const in Core).
    private const int InlineCodeTimeoutSeconds = Knotarium.Core.Domain.InlineCodeNodeDefaults.TimeoutSeconds;

    private void Register(NodePackageManifest manifest)
    {
        _manifests[manifest.Id] = manifest;
    }

    public Task<NodePackageManifest?> GetManifestAsync(NodePackageId packageId, CancellationToken cancellationToken = default)
    {
        foreach (var kvp in _manifests)
        {
            if (kvp.Key.Value.Equals(packageId.Value, StringComparison.OrdinalIgnoreCase))
            {
                return Task.FromResult<NodePackageManifest?>(kvp.Value);
            }
        }
        return Task.FromResult<NodePackageManifest?>(null);
    }

    public IReadOnlyCollection<NodePackageManifest> GetAllManifests()
    {
        return new ReadOnlyCollection<NodePackageManifest>(_manifests.Values.ToList());
    }
}
