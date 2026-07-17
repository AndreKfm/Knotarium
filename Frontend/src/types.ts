// Copyright 2026 Andre Kaufmann
// SPDX-License-Identifier: Apache-2.0

export type NodeId = {
  value: string;
};

export interface NodeDefinition {
  id: NodeId;
  type: string;
  properties: Record<string, unknown>;
}

export interface EdgeDefinition {
  id: string;
  from: NodeId;
  output: string;
  to: NodeId;
  input: string;
}

export interface WorkflowGroup {
  id: string;      // Stable generated ID, never derived from name (e.g. grp_01HZY...)
  name: string;    // Editable display name (trim first, then validate <= 80 chars)
  color: string;   // Hex color string (validated format #RRGGBB)
}

export interface WorkflowGroupContainer {
  version: number;
  groups: WorkflowGroup[];
}

export type FailureAlertMode = 'Inherit' | 'Off' | 'Custom';

export interface FailureAlertConfig {
  mode: FailureAlertMode;
  channelIds?: string[] | null;
}

export interface WorkflowMetadata {
  group?: string | null;  // Association reference
  failureAlert?: FailureAlertConfig | null;
}

// ── Authentication ───────────────────────────────────────────────────────────
export interface AuthStatus {
  /** False when the server runs in no-auth / single-operator mode (Auth:Enabled=false): no login at all. */
  enabled: boolean;
  authenticated: boolean;
  username: string | null;
  userId: string | null;
  /** True when the instance has no users yet — the first-run "create admin" screen applies. */
  setupRequired: boolean;
}

export interface AuthUser {
  id: string;
  username: string;
  role: string;
  createdAt: string;
}

export type NotificationChannelType = 'Webhook' | 'Slack' | 'Email';

export interface NotificationChannel {
  id: string;
  name: string;
  type: NotificationChannelType;
  isDefaultFailureAlert: boolean;
  createdAt: string;
  updatedAt: string;
}

export interface WorkflowDefinition {
  id: { value: string };
  name: string;
  nodes: NodeDefinition[];
  edges: EdgeDefinition[];
  metadata?: WorkflowMetadata | null;
  hasActiveVersion?: boolean;
  isEnabled?: boolean;
}

/** Access a file node may make against a granted path (Settings → File Access). */
export type FileAccessRuleMode = 'read' | 'write' | 'both';

/** One permitted directory subtree for the file nodes: everything under `path` for the given `mode`. */
export interface FileAccessRuleDto {
  path: string;
  mode: FileAccessRuleMode;
}

/**
 * Instance-global file-access policy enforced by the file nodes (Settings → File Access).
 * Deny-by-default: with `totalAccess` off and no `rules`, every file operation is blocked.
 * `minFreeBytes` / `minFreePercent` reserve free space on the target drive for writes (stricter wins).
 */
export interface FileAccessPolicyDto {
  totalAccess: boolean;
  rules: FileAccessRuleDto[];
  minFreeBytes: number | null;
  minFreePercent: number | null;
}

/** Enabled privileged node capabilities (Settings → Capabilities). Empty ⇒ all switchable capabilities off. */
export interface CapabilityPolicyDto {
  enabledCapabilities: string[];
}

/** The active AI provider config (Settings → AI Provider). The API key itself is never returned. */
export interface AiProviderConfigResponse {
  vendor: string | null;
  model: string | null;
  credentialRef: string | null;
  baseUrl: string | null;
  apiVersion: string | null;
  maxTokens: number | null;
  availableVendors: string[];
}

export interface SetAiProviderConfigInput {
  vendor: string;
  model: string;
  credentialRef: string;
  baseUrl?: string | null;
  apiVersion?: string | null;
  maxTokens?: number | null;
}

/** Result of POST /api/settings/ai-provider/test — a real mini-completion against the supplied config. */
export interface AiProviderTestResponse {
  ok: boolean;
  message: string;
  latencyMs: number | null;
  model: string | null;
}

/** Result of POST /api/settings/ai-provider/models — best-effort live model ids (empty = use curated). */
export interface AiProviderModelsResponse {
  models: string[];
}

export type AiGenerationStatus = 'Queued' | 'Running' | 'Succeeded' | 'Failed';

/** Poll result for an AI workflow-generation job (GET /api/ai/generate/{jobId}). */
export interface AiGenerationJobResult {
  jobId: string;
  status: AiGenerationStatus | string;
  /** Present only when Succeeded — the generated definition (topology only; geometry is laid out client-side). */
  workflow: WorkflowDefinition | null;
  /** Credential slot keys the generated workflow references but leaves unbound. */
  openSlots: string[];
  /** Compiler/parse errors when the repair loop gave up (Failed). */
  diagnostics: string[];
  attempts: number;
  /** A transport/config failure (e.g. the API key isn't set) — distinct from repairable diagnostics. */
  error: string | null;
}

export interface WorkflowVersion {
  id: string;
  workflowDefinitionId: { value: string };
  versionNumber: number;
  nodes: NodeDefinition[];
  edges: EdgeDefinition[];
  createdAt: string;
}

export type WorkflowVersionOrigin = 'Published' | 'Restored' | 'Imported';

/**
 * Lightweight version metadata returned by the paginated list endpoint
 * (GET /api/workflows/{id}/versions). Deliberately excludes nodes/edges — fetch
 * the detail endpoint (getWorkflowVersionDetail) when the full payload is needed.
 */
export interface WorkflowVersionSummary {
  id: string;
  versionNumber: number;
  createdAt: string;
  createdBy: string | null;
  label: string | null;
  origin: WorkflowVersionOrigin;
  isActive: boolean;
  restoredFromVersionId: string | null;
  nodeCount: number;
  executionCount: number;
}

/** Paginated envelope returned by GET /api/workflows/{id}/versions. */
export interface WorkflowVersionListResponse {
  items: WorkflowVersionSummary[];
  page: number;
  pageSize: number;
  totalCount: number;
}

export interface ActiveWorkflowVersion {
  workflowDefinitionId: { value: string };
  workflowVersionId: string;
  activatedAtUtc: string;
}

/**
 * Shape returned by POST /api/workflows/{id}/restore/{versionId}. A restore is
 * fork-forward: it always creates a new immutable version copied from the source.
 * With `activate=true` the new version is also activated (requiring a clean
 * compile); with `activate=false` it is an inactive forward copy and `warnings`
 * carries any compatibility findings.
 */
export interface RestoreVersionResult {
  versionId: string;
  versionNumber: number;
  origin: WorkflowVersionOrigin;
  restoredFromVersionId: string | null;
  activated: boolean;
  activatedAtUtc: string | null;
  warnings: string[];
}

export type NodeStatus = 'Pending' | 'Running' | 'Completed' | 'Failed' | 'Waiting' | 'RequiresManualDecision' | 'Retrying' | 'Cancelled';
export type ExecutionStatus = 'Pending' | 'Running' | 'Completed' | 'Failed' | 'Suspended' | 'WaitingForRetry' | 'Cancelled';

export interface NodeState {
  id: string;
  executionInstanceId: string;
  nodeId: NodeId;
  status: NodeStatus;
  inputs: Record<string, unknown>;
  outputs: Record<string, unknown>;
  errorMessage?: string;
  executionCount: number;
  /** JSON snapshot of the global variables as they were when this node started (for time-travel inspection). */
  variablesBefore?: string;
}

export interface ExecutionJournal {
  id: string;
  executionInstanceId: string;
  nodeId?: NodeId;
  timestamp: string;
  eventType: string;
  message: string;
  data: Record<string, unknown>;
}

/** One operand ref resolved against the last run (Condition editor "Last run" value source). */
export interface ConditionLastRunValue {
  found: boolean;
  value?: unknown;
  sensitive: boolean;
}

/** Response of POST /api/workflows/{id}/condition-values — last-run operand values + run provenance. */
export interface ConditionLastRunResponse {
  runId: string | null;
  versionId: string | null;
  createdAt: string | null;
  stale: boolean;
  values: Record<string, ConditionLastRunValue>;
}

export interface ExecutionInstance {
  id: string;
  workflowDefinitionId: { value: string };
  workflowVersionId?: string;
  status: ExecutionStatus;
  createdAt: string;
  updatedAt: string;
  workflowName?: string;
  triggerOrigin?: string;
  globalVariables: Record<string, unknown>;
  nodeStates: NodeState[];
  replayOfExecutionId?: string;
  replayFromNodeId?: string;
  errorOfExecutionId?: string;
}

export interface ReplayWarning {
  nodeId: string;
  sideEffectKind: string;
}

export interface ReplayResult {
  newExecutionId: string;
  warnings: ReplayWarning[];
}

export interface ReplayLineageEntry {
  id: string;
  status: ExecutionStatus;
  createdAt: string;
  updatedAt: string;
  triggerOrigin?: string;
  replayOfExecutionId: string;
  replayFromNodeId: string;
}

export interface CompilationDiagnostic {
  severity: 'Info' | 'Warning' | 'Error';
  code: string;
  message: string;
  nodeId?: NodeId;
  edgeId?: string;
}

export interface CompilationErrorResponse {
  message: string;
  diagnostics: CompilationDiagnostic[];
}

export interface NodePackageVersionSummary {
  id: string;
  nodePackageId: string;
  version: string;
  manifestJson: string;
  source: string;
  capabilities: string[];
  createdAt: string;
}

export interface NodePackageSummary {
  id: string;
  displayName: string;
  category: string;
  versions: NodePackageVersionSummary[];
}

// ── Integration-library bundles (.kgbundle) ──────────────────────────────────
// Shapes returned by POST /api/bundles/install. NOTE: the API has no global
// JsonStringEnumConverter, so the verification enums serialize as **integers**,
// not strings — see the *_LABELS maps in BundleInstaller for the legend. The
// install response carries the full report on 200 (installed), 422 (verification
// rejected) and 409 (version conflict) alike.

export interface CredentialSummary {
  id: string;
  name: string;
}

// ── Templates (.kgtpl) ──────────────────────────────────────────────────────

/** A symbolic credential slot a template declares; rebound to a real credential id at install. */
export interface TemplateCredentialSlot {
  slot: string;
  displayName: string;
  description?: string | null;
  requiredCredentialType?: string | null;
}

/** The declared type of a template parameter; drives the install-form input + coercion. */
export type TemplateParameterType = 'string' | 'number' | 'boolean' | 'enum';

/** A non-secret value the author left blank for the installer to supply ({{param:key}} in the graph). */
export interface TemplateParameter {
  key: string;
  label: string;
  description?: string | null;
  type: TemplateParameterType;
  options?: string[] | null;
  default?: string | null;
  required: boolean;
}

/** A map of parameter key → raw string value, supplied at install/insert. */
export type ParameterValues = Record<string, string>;

/** template.json — authoring metadata plus declared credential slots + parameters. */
export interface TemplateManifest {
  templateId: string;
  templateVersion: string;
  schemaVersion: number;
  name: string;
  author: string;
  description: string;
  tags: string[];
  category: string;
  minEngineVersion?: string | null;
  createdAtUtc: string;
  sourceWorkflowName: string;
  workflowChecksum: string;
  credentialSlots: TemplateCredentialSlot[];
  parameters: TemplateParameter[];
}

/** Whether a template's workflow can run on this engine, with human-readable warnings. */
export interface TemplateCompatibility {
  supported: boolean;
  warnings: string[];
}

/** A node in an imported workflow that carries a privileged capability (filesystem/code/database). */
export interface PrivilegedNodeInfo {
  nodeType: string;
  displayName: string;
  capabilities: string[];
}

/** Result of inspecting an uploaded .kgtpl without importing it. */
export interface TemplateInspectResponse {
  manifest: TemplateManifest;
  credentialSlots: TemplateCredentialSlot[];
  compatibility: TemplateCompatibility;
  privilegedNodes: PrivilegedNodeInfo[];
}

/** Result of installing a template as a new draft workflow. */
export interface TemplateInstallResponse {
  workflowId: string;
  versionNumber: number;
  workflowName: string;
  reboundSlots: string[];
  openSlots: string[];
  bindingErrors: string[];
  configurationRequired: boolean;
  runnable: boolean;
  diagnostics: string[];
}

/** A built-in gallery entry: a template id paired with its parsed manifest. */
export interface GalleryTemplate {
  templateId: string;
  manifest: TemplateManifest;
}

/** A template's graph (+ metadata/compatibility) for inserting into the open workflow. */
export interface TemplatePayloadResponse {
  manifest: TemplateManifest;
  credentialSlots: TemplateCredentialSlot[];
  compatibility: TemplateCompatibility;
  nodes: NodeDefinition[];
  edges: EdgeDefinition[];
}

/** Request body for exporting a workflow as a template. */
export interface TemplateExportRequest {
  workflowId: string;
  name?: string;
  author?: string;
  description?: string;
  tags?: string[];
  category?: string;
  templateVersion?: string;
  parameters?: TemplateParameter[];
}

/** What credential references the export lifted into slots, for user review. */
export interface TemplatePortabilizationReport {
  slots: TemplateCredentialSlot[];
  rewrittenPaths: string[];
}

/** A symbolic credential slot the bundle requires; rebound to a real credential id at install. */
export interface BundleCredentialSlot {
  slot: string;
  type: string;
  displayName: string;
  description?: string | null;
  checklist: string[];
}

/** Per-package verdict from the install gate. signatureStatus / trustLevel / status are integer enums. */
export interface BundlePackageVerification {
  packageId: string;
  expectedSha256: string;
  actualSha256: string | null;
  hashMatches: boolean;
  signatureVerified: boolean;
  /** 0 = NotPresent, 1 = PresentUntrusted, 2 = VerifiedTrusted */
  signatureStatus: number;
  /** 0 = Untrusted, 1 = Provisional, 2 = Verified */
  trustLevel: number;
  /** 0 = Missing, 1 = HashMismatch, 2 = Untrusted, 3 = Provisional, 4 = Verified */
  status: number;
  installable: boolean;
}

/** An imported workflow: its manifest key plus the inactive version created for it. */
export interface BundleWorkflowInstall {
  key: string;
  workflowId: string;
  versionNumber: number;
}

export interface BundleInstallResponse {
  installed: boolean;
  installedPackages: string[];
  skippedPackages: string[];
  importedWorkflows: BundleWorkflowInstall[];
  requiredCredentialSlots: BundleCredentialSlot[];
  reboundCredentialSlots: string[];
  unboundCredentialSlots: string[];
  conflictingPackages: string[];
  verification: BundlePackageVerification[];
  blocking: BundlePackageVerification[];
  privilegedNodes: PrivilegedNodeInfo[];
  /** True when the install was held back only because privileged nodes weren't acknowledged. */
  privilegedAcknowledgementRequired: boolean;
}

// Manifest authoring intent for POST /api/bundles/export (bundle.json). camelCase
// keys bind to the backend's PascalCase record case-insensitively. Hash-free by
// design — resolution, hashing and trust are computed server-side into the lock.
export interface BundlePackageRefInput {
  id: string;
  /** A semver constraint or exact pin, e.g. ">=1.0.0" or "1.2.3". */
  versionConstraintOrPin: string;
  source: string;
}

export interface BundleWorkflowRefInput {
  /** The WorkflowDefinitionId of the workflow to include. */
  key: string;
  role: string;
  /** Filename under workflows/ in the archive. */
  ref: string;
}

export interface BundleProvenanceInput {
  source: string;
  publisher: string;
}

export interface BundleManifestInput {
  bundleId: string;
  bundleVersion: string;
  name: string;
  publisher: string;
  tags: string[];
  category: string;
  schemaVersion: number;
  minEngineVersion: string;
  packages: BundlePackageRefInput[];
  credentialSlots: BundleCredentialSlot[];
  workflows: BundleWorkflowRefInput[];
  provenance: BundleProvenanceInput;
}

/** A named, typed field within a node's declared input/output socket (design-time schema). */
export interface NodeFieldSchema {
  name: string;
  type?: string;
  required?: boolean;
}

/** A declared input/output socket on a node manifest (port name + optional payload schema). */
export interface NodeSocketSchema {
  name: string;
  type?: string;
  fields?: NodeFieldSchema[];
}

export interface NodePackageManifestSummary {
  id: string;
  displayName: string;
  category: string;
  triggerOnly?: boolean;
  outputs?: NodeSocketSchema[];
  inputs?: NodeSocketSchema[];
}

export interface WorkflowScheduleSummary {
  nodeId: string;
  cronExpression: string;
  timeZoneId: string;
  nextFireAtUtc: string;
  isActive: boolean;
}

export interface ParameterDefinition {
  name: string;
  type: 'string' | 'number' | 'boolean' | 'enum' | 'credentialRef' | 'notificationChannelRef' | 'code' | 'keyValue' | 'dynamicOptions' | 'resourceLocator' | 'dynamicFields' | 'agentTools' | 'aiModel';
  required?: boolean;
  values?: string[];
  default?: unknown;
  expression?: boolean;
  /** Optional human-facing hint rendered under the field in the properties form. */
  description?: string;
  // Dynamic-options / resource-locator fields (used when type is 'dynamicOptions'/'resourceLocator').
  optionsLoader?: string;
  integrationType?: string;
  dependsOn?: string[];
  loaderConfig?: Record<string, string>;
  allowManualEntry?: boolean;
  multiple?: boolean;
}

/** Spec-derived hint that an OpenAPI path parameter can be picked from a sibling collection. */
export interface LocatorSuggestion {
  name: string;
  in: string;
  collectionPath: string;
  valueField: string;
  labelField: string;
  dependsOn: string[];
}

/** A single selectable option returned by the design-time options endpoint. */
export interface OptionItem {
  label: string;
  value: string;
  description?: string;
  /**
   * Optional generic value-kind hint ("String" | "Integer" | "Number" | "Boolean" | "Enum" |
   * "DateTime" | …) so a consumer can build a typed sub-editor for this option. Undefined for
   * loaders that don't supply it.
   */
  kind?: string;
  /** Allowed values when {@link kind} is an enumeration; otherwise undefined. */
  enumValues?: string[];
}

/** Envelope returned by POST /api/integrations/{integrationType}/options/{loaderName}. */
export interface LoadOptionsResult {
  options: OptionItem[];
  hasMore: boolean;
  nextPage?: string | null;
  error?: { code: string; message: string } | null;
}

/** Persisted single-select value for a dynamic-options parameter. */
export interface DynamicOptionValue {
  value: string;
  label?: string;
  mode: 'list' | 'manual';
}

/** Persisted multi-select value for a dynamic-options parameter (order-preserving). */
export interface DynamicOptionMultiValue {
  mode: 'list' | 'manual';
  items: Array<{ value: string; label?: string }>;
}

export interface InlineCodeTestResult {
  success: boolean;
  output?: unknown;
  error?: string | null;
  logs: string[];
  elapsedMs: number;
}

export interface NodeManifest {
  id: string;
  displayName: string;
  parameters?: ParameterDefinition[];
  outputs?: NodeSocketSchema[];
  inputs?: NodeSocketSchema[];
}

// ── OpenAPI Importer types ───────────────────────────────────────────────────

export interface ImportedSpec {
  id: string;
  title: string;
  apiVersion: string;
  latestVersionNumber: number;
  importedAtUtc: string;
}

export interface ApiParameter {
  name: string;
  in: 'path' | 'query' | 'header' | 'cookie';
  required: boolean;
  description?: string;
  schemaJson: string;
}

export interface ApiRequestBody {
  required: boolean;
  mediaTypes: string[];
  schemaJson: string;
}

export interface ApiOperation {
  operationId: string;
  method: string;
  pathTemplate: string;
  summary?: string;
  tags: string[];
  parameters: ApiParameter[];
  requestBody?: ApiRequestBody;
}

export interface OperationGroup {
  tag: string;
  operations: ApiOperation[];
}

export interface ApiSchema {
  name: string;
  description?: string;
  schemaJson: string;
}

export interface SpecDetail {
  id: string;
  title: string;
  groups: OperationGroup[];
  schemas: ApiSchema[];
  defaultServers?: string[];
}

export interface ServerConfigInfo {
  id: string;
  name: string;
  baseUrl: string;
  serverVariables: Record<string, string>;
  securitySchemeType: string;
  credentialRef?: string | null;
  allowInsecureCertificate?: boolean;
  createdAt: string;
  updatedAt: string;
}

export interface CreateServerConfigRequest {
  name: string;
  baseUrl: string;
  serverVariables: Record<string, string>;
  securitySchemeType: string;
  credentialRef?: string | null;
  allowInsecureCertificate?: boolean;
}

// ── Backup & Restore (.kgbak) ──────────────────────────────────────────────

/** Self-describing header of a backup archive (no secrets); powers the restore preview. */
export interface BackupManifest {
  formatVersion: number;
  engineVersion: string;
  createdAtUtc: string;
  databaseProvider: string;
  includesRunHistory: boolean;
  /** Per-aggregate row counts, keyed by the archive's data-document name (e.g. "credentials.json"). */
  counts: Record<string, number>;
  /** How the archive is encrypted: "Passphrase" (portable) or "ServerKey" (restorable only on the creating host). */
  keySource?: string;
}

/** Outcome of a restore: where the auto pre-restore safety backup landed, plus what was restored. */
export interface RestoreReport {
  preRestoreBackupPath: string;
  manifest: BackupManifest;
  restored: Record<string, number>;
}

// ---- External signal systems (generic admin seam for an IExternalSignalProvider) -----------------
// Vendor-neutral: the provider supplies its own branding via ProviderDescriptor at runtime.

export interface ProviderDescriptor {
  providerId: string;
  displayName: string;
  systemNoun: string;
  targetNoun: string;
  channelNoun: string;
  supportsSync: boolean;
  supportsTestConnection: boolean;
  requiresCredentials: boolean;
}

export type TargetConnectivity = 'Offline' | 'Connecting' | 'Online' | 'Faulted';

export interface TargetStatus {
  targetId: string;
  // The host serializes this enum as its numeric value (0=Offline,1=Connecting,2=Online,3=Faulted);
  // tolerate the string form too. Normalize with connectivityLabel() before display/comparison.
  connectivity: TargetConnectivity | number;
  lastConnected?: string | null;
  lastSignal?: string | null;
  lastError?: string | null;
  failedDispatches: number;
}

export interface CatalogChannel {
  channelId: string;
  displayName: string;
  globalCameraNumber: number;
}

export interface CatalogEntry {
  id: string;
  displayName: string;
  description?: string | null;
}

export interface ExternalTargetInfo {
  id: string;
  name: string;
  host: string;
  port: number;
  user?: string | null;
  hasCredential: boolean;
  channels: CatalogChannel[];
  events: CatalogEntry[];
  actions: CatalogEntry[];
  status: TargetStatus;
  // Per-target: drop this device's own reflected outbound signals (self-echo). Defaults on.
  suppressSelfEcho?: boolean;
}

// Build identity shown on the dashboard so a stale instance is obvious (GET /api/version).
export interface VersionInfo {
  version: string;
  buildTimeUtc?: string | null;
}

// A provider-declared system-level boolean toggle (vendor-neutral: label/help come from the provider).
export interface SystemOption {
  key: string;
  label: string;
  value: boolean;
  description?: string | null;
}

export interface SystemMetric {
  key: string;
  label: string;
  value: string;
}

export interface SystemActivityEntry {
  timestamp: string;
  kind: string;
  summary: string;
  detail?: string | null;
}

// Live, observed-only provider diagnostics (never persisted; resets on host restart).
export interface SystemDiagnostics {
  metrics: SystemMetric[];
  recentActivity: SystemActivityEntry[];
}

export interface ExternalSystemInfo {
  id: string;
  name: string;
  targets: ExternalTargetInfo[];
  options?: SystemOption[] | null;
  diagnostics?: SystemDiagnostics | null;
}

// A null password means "leave the stored secret unchanged"; clearPassword removes it.
export interface ExternalTargetEdit {
  id?: string | null;
  name: string;
  host: string;
  port: number;
  user?: string | null;
  password?: string | null;
  clearPassword?: boolean;
  // null/omitted = leave unchanged (existing value, or provider default for a new target).
  suppressSelfEcho?: boolean | null;
}

// ── Vendor-setting import (plugin-contributed providers) ────────────────────
export type ImportGranularity = 'multiple' | 'single';

export type ImportTargetStrategy = 'CreateOrReuse' | 'MapToExisting' | 'DontMap';

export interface ImportProviderDescriptor {
  id: string;
  displayName: string;
  fileExtensions: string[];
  supportsGranularity: boolean;
  supportsTargetStrategy: boolean;
  defaultGranularity: ImportGranularity;
  description?: string | null;
}

export interface ImportServerRow {
  alias: string;
  host?: string | null;
  user?: string | null;
  enabled: boolean;
}

export interface ImportProvisionRow {
  serverAlias: string;
  action: 'Create' | 'Reuse' | 'Bind' | 'Skip';
  targetId?: string | null;
}

export interface ImportReportRow {
  scope: string;
  construct: string;
  outcome: 'Mapped' | 'Partial' | 'Flagged';
  reason?: string | null;
}

export interface ImportWorkflowSummary {
  id: string;
  name: string;
  nodes: number;
  edges: number;
}

export interface ImportPreviewResponse {
  granularity: ImportGranularity;
  workflows: ImportWorkflowSummary[];
  report: ImportReportRow[];
  servers: ImportServerRow[];
  provisioned: ImportProvisionRow[];
}

export interface ImportInstalledRow {
  value: string;
  name: string;
  versionNumber: number;
}

export interface ImportInstallResponse {
  granularity: ImportGranularity;
  installed: ImportInstalledRow[];
  report: ImportReportRow[];
  servers: ImportServerRow[];
  provisioned: ImportProvisionRow[];
}
