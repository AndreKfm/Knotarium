# Parameterized templates — concept (design only, not yet built)

## Why
Today a template carries two abstractions: the **workflow graph** and **credential slots**
(`slot:<key>` placeholders rebound at install). Many useful templates also need **non-secret
configuration** the author can't hardcode — a Slack channel, a base URL, a polling interval, a
sender address. Parameters let a template *ask* for those values at install/insert time and
substitute them into the graph, the same way credential slots rebind credentials.

## Shape
Extend `TemplateManifest` (`Backend/Knotarium.Api/Services/Templates/TemplateModel.cs`) with:

```
parameters: TemplateParameter[]
TemplateParameter {
  key: string            // stable token used in the graph, e.g. "slack_channel"
  label: string          // shown in the install dialog
  description?: string
  type: "string" | "number" | "boolean" | "enum"
  options?: string[]     // for enum
  default?: string
  required: boolean
}
```
The graph references a parameter with a token in any node property value:
**`{{param:slack_channel}}`** (distinct from `slot:` so the two never collide).

## Author-time (export)
Two options to decide:
- **Explicit:** an "Add parameter" step in the exporter where the author declares parameters and
  inserts `{{param:key}}` tokens into node fields. Most predictable.
- **Detected:** export scans node properties for `{{param:key}}` tokens and lists the keys, prompting
  the author to label/type them. Less manual but needs a token convention users adopt.

Recommendation: start **explicit** (declared in the exporter), detection later.

## Install / insert-time
- The install dialog (`TemplateImporter`, gallery confirm, and the canvas `TemplateInsertPicker`)
  renders a **values form** — reuse the `CredentialSlotBinding` row pattern, one row per parameter,
  typed input (text / number / toggle / select). Required-but-empty blocks the action, mirroring
  the slot-binding gate.
- Substitution reuses the existing recursive node-property walk in
  `Backend/Knotarium.Api/Services/WorkflowPortability/CredentialSlotModule.cs` — add a **third
  direction** alongside `ExtractIdsToSlots` / `RebindSlotsToIds`:
  `SubstituteParameters(document, values)` replacing `{{param:key}}` (whole-value or embedded in a
  string) with the supplied value.

## Backend touch points (when built)
- `TemplateManifest.parameters` + serializer round-trip.
- `TemplateInstallService` / `TemplatePayloadService` accept a `parameterValues` map; substitute
  before import / before returning the graph.
- Endpoints (`/api/templates/install`, `/api/templates/gallery/{id}/install`,
  `/api/templates/payload`) gain a `parameterValues` form/JSON field.
- Inspect returns the declared `parameters` so the UI can render the form before committing.

## Open questions to resolve before implementing
1. **Authoring model** — explicit declaration vs. token detection (recommend explicit first).
2. **Typing & validation** — number/enum coercion, required handling, defaults; how invalid values
   are reported (parallel to `TemplateBindingException`).
3. **Embedded vs. whole-value tokens** — only replace a property whose entire value is the token, or
   also interpolate `"https://{{param:host}}/api"`? Interpolation is more powerful but complicates
   typing (everything becomes string).
4. **Interaction with credential slots** — order of substitution (params first, then slots), and
   whether a parameter may target a credential slot.
5. **Inline insert** — parameters must be collected in the `TemplateInsertPicker` too, not only the
   create-workflow flows.

## Out of scope for the first cut
Conditional/derived parameters, parameter groups, per-parameter regex validation — revisit after the
basic string/number/bool/enum substitution ships.
