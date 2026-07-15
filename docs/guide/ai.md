# AI provider & AI nodes

Knotarium's AI nodes run large-language-model calls over the data flowing through a workflow.
You configure **one** AI provider for the instance, and every AI node uses it.

## Configure a provider

Open **Settings → AI Provider** and set:

- **Provider** — Anthropic (Claude), OpenAI (ChatGPT), Azure OpenAI, or Google Gemini. Any
  OpenAI-compatible endpoint (a local runtime such as Ollama / vLLM / LM Studio) works via the
  **Base URL** field.
- **Model** — pick from the suggestions or type your own. The field is an editable combo: it
  offers a curated per-vendor list, accepts any custom model, and the **↻ live** button loads
  the provider's real model list.
- **API key** — stored as an **encrypted credential**, never in plain text and never in a
  workflow. Add one inline with **+ Add key**.

Then click **Test connection** — it runs a tiny real completion and reports success + latency,
so you know the provider, key, and model all work before you build anything.

::: tip Model override per node
Every AI node also has an optional **model** field, so one node can use a different model than
the instance default.
:::

## The AI nodes

All four take their prompt/inputs as expression fields, so references to upstream data are
already evaluated when the node runs.

- **AI Prompt** — one LLM call over the incoming data (summarise, extract, classify, draft).
  In *JSON mode* (set a schema) it returns a parsed object instead of raw text.
- **AI Router** — classifies the input and routes the run down one of your category branches
  (plus an `otherwise` fallback). The categories are the node's output ports.
- **AI Verify** — an **evidence gate**. It checks a text's factual claims against the sources
  you supply, claim-by-claim, and routes by the overall verdict
  (`verified` / `unsupported` / `contradicted` / `uncertain`). The verdict is decided by
  deterministic code over the model's structured findings — not a second "does this look right?"
  prompt — so a claim with no supporting evidence is downgraded, never assumed true.
- **AI Semantic Diff** — compares two versions of a document by meaning and routes by whether
  anything **material** changed (vs cosmetic vs none). Identical inputs short-circuit with no
  model call.

Try them from the gallery: **AI Summarize**, **AI Evidence Check**, **AI Contract Diff**, and
the **AI Support Triage** starter chain them together.

## The AI Agent

The **AI Agent** node runs a bounded LLM **tool-use loop** where the *tools are your own
workflows*. You allowlist a set of workflows on the node; the model decides which to call with
which arguments, each call runs as a real, journaled child run, its outputs feed back, and the
loop iterates to a final answer.

This is the differentiator: instead of a foreign tool ecosystem, **the tool surface is
Knotarium**. Anything you can build as a workflow — an HTTP lookup, a DB query, an email send —
becomes an agent capability, under all the existing security policy, because a tool call *is* a
normal run.

### Enabling and using it

1. **Enable the capability.** The node is off by default — an administrator turns on the
   **AI Agent** capability under **Settings → Capabilities**.
2. **Choose a tool-capable model.** The agent needs a model that supports function tools on the
   Chat Completions API — e.g. `gpt-4o`, `gpt-4.1`, or a comparable Anthropic/local model.

   ::: warning OpenAI reasoning models
   OpenAI **reasoning** models (the gpt-5 family) are **not yet supported** for the agent's tool
   loop — set a per-node `model` override such as `gpt-4o`, or pick a non-reasoning model.
   Full reasoning-model tool support (via OpenAI's `/v1/responses` API) is planned. The other
   AI nodes work fine with reasoning models.
   :::
3. **List tools deliberately.** Each tool is a workflow you allowlist. **List only workflows you
   would let the incoming data invoke** — data in the task and in tool results is untrusted. The
   containment is structural: an injected instruction can at worst steer the agent *within the
   tools you listed and its iteration budget*; it can never call a workflow you didn't list.
4. **Tool workflows are plain functions.** A tool workflow must begin with a plain Start node
   (not a webhook/schedule/poll/error trigger) and may not itself contain an AI Agent node.

The **Order Concierge** starter template (plus its companion **Order Status** tool workflow)
shows the whole pattern end-to-end — install both, wire the tool on the agent node, and run.
