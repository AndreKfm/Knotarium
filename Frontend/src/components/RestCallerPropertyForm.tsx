// Copyright 2026 Andre Kaufmann
// SPDX-License-Identifier: Apache-2.0

import { useEffect, useState } from 'react';
import type { ApiOperation, LocatorSuggestion, ParameterDefinition, ServerConfigInfo, SpecDetail } from '../types';
import { getOperation, listServerConfigs, getSpecDetail, getLocatorSuggestions } from '../utils/openApiClient';
import { FieldDropWrapper } from './shared/ManifestForm';
import { AsyncOptionsField } from './shared/AsyncOptionsField';
import { OperationPicker } from './OperationPicker';

/** Per-argument resource-locator configuration, persisted under arguments._locators. */
interface LocatorConfig {
  enabled?: boolean;
  path?: string;
  labelField?: string;
  valueField?: string;
  dependsOn?: string[];
}

/** Synthesizes the ParameterDefinition that drives AsyncOptionsField for one OpenAPI argument. */
function buildLocatorParam(name: string, locator: LocatorConfig): ParameterDefinition {
  return {
    name,
    type: 'resourceLocator',
    optionsLoader: 'rest.collection',
    integrationType: 'generic',
    allowManualEntry: true,
    dependsOn: locator.dependsOn,
    loaderConfig: {
      path: locator.path ?? '',
      labelField: locator.labelField || 'name',
      valueField: locator.valueField || 'id',
    },
  };
}

export interface RestCallerPropertyFormProps {
  workflowId?: string | null;
  specId: string;
  operationId: string;
  arguments: Record<string, unknown>;
  onArgumentsChange: (args: Record<string, unknown>) => void;
  onOperationIdChange: (operationId: string) => void;
  serverConfigId?: string;
  onServerConfigIdChange: (id: string) => void;
}

export function RestCallerPropertyForm({
  workflowId,
  specId,
  operationId,
  arguments: args,
  onArgumentsChange,
  onOperationIdChange,
  serverConfigId,
  onServerConfigIdChange,
}: RestCallerPropertyFormProps) {
  const [specDetail, setSpecDetail] = useState<SpecDetail | null>(null);
  const [operationDetail, setOperationDetail] = useState<ApiOperation | null>(null);
  const [serverConfigs, setServerConfigs] = useState<ServerConfigInfo[]>([]);
  const [locatorSuggestions, setLocatorSuggestions] = useState<Record<string, LocatorSuggestion>>({});

  const [loadingSpec, setLoadingSpec] = useState(false);
  const [loadingOperation, setLoadingOperation] = useState(false);
  const [loadingConfigs, setLoadingConfigs] = useState(false);

  useEffect(() => {
    setLoadingConfigs(true);
    listServerConfigs()
      .then(setServerConfigs)
      .catch(err => console.error('Error loading server configs:', err))
      .finally(() => setLoadingConfigs(false));
  }, []);

  useEffect(() => {
    if (!specId) {
      setSpecDetail(null);
      return;
    }
    setLoadingSpec(true);
    getSpecDetail(specId)
      .then(setSpecDetail)
      .catch(err => console.error('Error loading spec details:', err))
      .finally(() => setLoadingSpec(false));
  }, [specId]);

  useEffect(() => {
    if (!specId || !operationId) {
      setOperationDetail(null);
      return;
    }
    setLoadingOperation(true);
    getOperation(specId, operationId)
      .then(setOperationDetail)
      .catch(err => console.error('Error loading operation details:', err))
      .finally(() => setLoadingOperation(false));
  }, [specId, operationId]);

  // Spec-derived resource-locator hints for this operation's path params (auto-fill on enable).
  useEffect(() => {
    if (!specId || !operationId) {
      setLocatorSuggestions({});
      return;
    }
    getLocatorSuggestions(specId, operationId)
      .then(list => setLocatorSuggestions(Object.fromEntries(list.map(s => [s.name, s]))))
      .catch(err => {
        console.error('Error loading locator suggestions:', err);
        setLocatorSuggestions({});
      });
  }, [specId, operationId]);

  const handleFieldChange = (category: 'path' | 'query' | 'header' | 'body', fieldName: string, val: unknown) => {
    const currentCategoryArgs = (args[category] as Record<string, unknown>) || {};
    const newArgs = {
      ...args,
      [category]: {
        ...currentCategoryArgs,
        [fieldName]: val,
      },
    };
    onArgumentsChange(newArgs);
  };

  // Resource-locator config lives under arguments._locators, which the executor ignores — so the
  // selected value (a stable key) stays at arguments[category][name] and flows into the URL as-is.
  const getLocator = (category: string, name: string): LocatorConfig =>
    ((args._locators as Record<string, Record<string, LocatorConfig>> | undefined)?.[category]?.[name]) ?? {};

  const setLocator = (category: string, name: string, cfg: LocatorConfig) => {
    const locators = (args._locators as Record<string, Record<string, LocatorConfig>>) || {};
    const catLocators = locators[category] || {};
    onArgumentsChange({
      ...args,
      _locators: { ...locators, [category]: { ...catLocators, [name]: cfg } },
    });
  };

  const cfgInputStyle: React.CSSProperties = {
    flex: 1, padding: '7px 9px', borderRadius: '6px', background: 'rgba(0,0,0,0.2)',
    border: '1px solid var(--border-color, rgba(255,255,255,0.1))', color: '#fff', fontSize: '0.78rem', outline: 'none',
  };

  const renderField = (category: 'path' | 'query' | 'header' | 'body', name: string, required: boolean, description?: string) => {
    const value = (args[category] as Record<string, unknown>)?.[name] ?? '';
    // Resource locators make sense for path/query identifiers; headers/body stay plain text.
    const locatorEligible = category === 'path' || category === 'query';
    const locator = getLocator(category, name);
    const locatorOn = locatorEligible && !!locator.enabled;
    const suggestion = locatorEligible ? locatorSuggestions[name] : undefined;
    // Sibling path values feed cascading dependsOn placeholders in the collection path.
    const pathArgs = (args.path as Record<string, unknown>) || {};

    const toggleLocator = () => {
      const turnOn = !locator.enabled;
      // Auto-fill from the spec-derived suggestion the first time it's enabled.
      const next: LocatorConfig = turnOn && suggestion && !locator.path
        ? {
            enabled: true,
            path: suggestion.collectionPath,
            labelField: suggestion.labelField,
            valueField: suggestion.valueField,
            dependsOn: suggestion.dependsOn,
          }
        : { ...locator, enabled: turnOn };
      setLocator(category, name, next);
    };

    return (
      <div key={`${category}-${name}`} style={{ display: 'flex', flexDirection: 'column', gap: '6px', marginBottom: '14px' }}>
        <div style={{ display: 'flex', justifyContent: 'space-between', alignItems: 'center' }}>
          <label style={{ display: 'block', fontSize: '0.75rem', fontWeight: 700, color: 'var(--text-secondary, #94a3b8)', textTransform: 'uppercase' }}>
            {name} {required && <span style={{ color: 'var(--color-error, #f87171)' }}>*</span>}
          </label>
          {locatorEligible && (
            <button
              type="button"
              onClick={toggleLocator}
              style={{ background: 'transparent', border: '1px solid var(--border-color, rgba(255,255,255,0.1))', borderRadius: '6px', color: 'var(--text-secondary, #94a3b8)', fontSize: '0.68rem', padding: '3px 8px', cursor: 'pointer' }}
              title="Pick this value from a live resource list instead of typing it"
            >
              {locatorOn ? '⌨ Type value' : '⚲ Pick from list'}
            </button>
          )}
        </div>
        {description && (
          <span style={{ fontSize: '0.7rem', color: 'var(--text-muted, #64748b)', marginBottom: '2px' }}>
            {description}
          </span>
        )}
        {!locatorOn && suggestion && (
          <span style={{ fontSize: '0.7rem', color: 'var(--color-accent, #6366f1)', marginBottom: '2px' }}>
            ⚲ Auto-detected: can be picked from <code>GET {suggestion.collectionPath}</code>
          </span>
        )}

        {locatorOn ? (
          <>
            {/* Loader config: which collection endpoint + which fields map to label/value. */}
            <div style={{ display: 'flex', gap: '6px' }}>
              <input
                type="text" value={locator.path ?? ''} placeholder="collection path e.g. pets"
                onChange={(e) => setLocator(category, name, { ...locator, path: e.target.value })}
                style={cfgInputStyle}
              />
              <input
                type="text" value={locator.labelField ?? ''} placeholder="label field (name)"
                onChange={(e) => setLocator(category, name, { ...locator, labelField: e.target.value })}
                style={{ ...cfgInputStyle, flex: '0 0 30%' }}
              />
              <input
                type="text" value={locator.valueField ?? ''} placeholder="value field (id)"
                onChange={(e) => setLocator(category, name, { ...locator, valueField: e.target.value })}
                style={{ ...cfgInputStyle, flex: '0 0 30%' }}
              />
            </div>
            <AsyncOptionsField
              param={buildLocatorParam(name, locator)}
              value={value}
              properties={pathArgs}
              connectionId={serverConfigId}
              onChange={(val) => handleFieldChange(category, name, val)}
            />
          </>
        ) : (
          <>
            <FieldDropWrapper workflowId={workflowId} value={value} onChange={(val) => handleFieldChange(category, name, val)}>
              <input
                type="text"
                value={value && typeof value === 'object' && (value as any).__type === 'variable_ref' ? '' : (typeof value === 'object' && value !== null ? ((value as any).label ?? (value as any).value ?? '') : (value as string))}
                onChange={(e) => handleFieldChange(category, name, e.target.value)}
                placeholder={`Enter ${name}...`}
                style={{
                  width: '100%',
                  padding: '10px',
                  borderRadius: '8px',
                  background: 'rgba(0, 0, 0, 0.2)',
                  border: '1px solid var(--border-color, rgba(255, 255, 255, 0.1))',
                  color: '#fff',
                  fontSize: '0.85rem',
                  outline: 'none',
                }}
              />
            </FieldDropWrapper>
            {!(value as any)?.__type && (
              <span style={{ fontSize: '0.7rem', color: 'var(--text-muted, #64748b)', marginTop: '2px' }}>
                Supports expressions: <code>{"{{ $node.X.output }}"}</code>
              </span>
            )}
          </>
        )}
      </div>
    );
  };

  const operationGroups = specDetail ? specDetail.groups : [];

  const pathParams = operationDetail
    ? operationDetail.parameters.filter(p => p.in === 'path')
    : [];

  const queryParams = operationDetail
    ? operationDetail.parameters.filter(p => p.in === 'query')
    : [];

  const headerParams = operationDetail
    ? operationDetail.parameters.filter(p => p.in === 'header' || p.in === 'cookie')
    : [];

  let bodyFields: Array<{ name: string; required: boolean; description?: string }> = [];
  if (operationDetail?.requestBody?.schemaJson) {
    try {
      const schema = JSON.parse(operationDetail.requestBody.schemaJson);
      if (schema && typeof schema === 'object') {
        if (schema.type === 'object' && schema.properties) {
          bodyFields = Object.keys(schema.properties).map(key => {
            const prop = schema.properties[key];
            const isRequired = Array.isArray(schema.required) && schema.required.includes(key);
            return {
              name: key,
              required: !!isRequired,
              description: prop?.description,
            };
          });
        }
      }
    } catch (err) {
      console.error('Error parsing request body schemaJson:', err);
    }
  }

  const hasNoParams =
    pathParams.length === 0 &&
    queryParams.length === 0 &&
    headerParams.length === 0 &&
    bodyFields.length === 0;

  return (
    <div style={{ display: 'flex', flexDirection: 'column', gap: '20px' }}>
      {/* Operation dropdown */}
      <div style={{ display: 'flex', flexDirection: 'column', gap: '6px' }}>
        <span style={{ display: 'block', fontSize: '0.75rem', fontWeight: 700, color: 'var(--text-secondary, #94a3b8)', textTransform: 'uppercase' }}>
          Operation <span style={{ color: 'var(--color-error, #f87171)' }}>*</span>
        </span>
        {loadingSpec ? (
          <span style={{ fontSize: '0.8rem', color: 'var(--text-muted, #64748b)' }}>Loading operations...</span>
        ) : (
          <OperationPicker
            groups={operationGroups}
            value={operationId}
            onChange={onOperationIdChange}
          />
        )}
      </div>

      {/* Server Config dropdown */}
      <div style={{ display: 'flex', flexDirection: 'column', gap: '6px' }}>
        <label htmlFor="server-config-select" style={{ display: 'block', fontSize: '0.75rem', fontWeight: 700, color: 'var(--text-secondary, #94a3b8)', textTransform: 'uppercase' }}>
          Server Config <span style={{ color: 'var(--color-error, #f87171)' }}>*</span>
        </label>
        {loadingConfigs ? (
          <span style={{ fontSize: '0.8rem', color: 'var(--text-muted, #64748b)' }}>Loading configs...</span>
        ) : (
          <select
            id="server-config-select"
            value={serverConfigId || ''}
            onChange={(e) => onServerConfigIdChange(e.target.value)}
            style={{
              width: '100%',
              padding: '10px',
              borderRadius: '8px',
              background: 'var(--bg-surface-opaque, rgba(20, 20, 20, 0.8))',
              border: '1px solid var(--border-color, rgba(255, 255, 255, 0.1))',
              color: '#fff',
              fontSize: '0.85rem',
              outline: 'none',
            }}
          >
            <option value="">Select server config...</option>
            {serverConfigs.map(config => (
              <option key={config.id} value={config.id}>
                {config.name} ({config.baseUrl})
              </option>
            ))}
          </select>
        )}
      </div>

      {/* Parameters */}
      {loadingOperation ? (
        <span style={{ fontSize: '0.85rem', color: 'var(--text-muted, #64748b)' }}>Loading operation parameters...</span>
      ) : (
        <>
          {pathParams.length > 0 && (
            <div style={{ marginTop: '8px' }}>
              <h3 style={{ fontSize: '0.8rem', fontWeight: 700, color: 'var(--color-accent, #6366f1)', textTransform: 'uppercase', marginBottom: '10px', borderBottom: '1px solid var(--border-color, rgba(255,255,255,0.1))', paddingBottom: '4px' }}>Path Parameters</h3>
              {pathParams.map(p => renderField('path', p.name, p.required, p.description))}
            </div>
          )}

          {queryParams.length > 0 && (
            <div style={{ marginTop: '8px' }}>
              <h3 style={{ fontSize: '0.8rem', fontWeight: 700, color: 'var(--color-accent, #6366f1)', textTransform: 'uppercase', marginBottom: '10px', borderBottom: '1px solid var(--border-color, rgba(255,255,255,0.1))', paddingBottom: '4px' }}>Query Parameters</h3>
              {queryParams.map(p => renderField('query', p.name, p.required, p.description))}
            </div>
          )}

          {headerParams.length > 0 && (
            <div style={{ marginTop: '8px' }}>
              <h3 style={{ fontSize: '0.8rem', fontWeight: 700, color: 'var(--color-accent, #6366f1)', textTransform: 'uppercase', marginBottom: '10px', borderBottom: '1px solid var(--border-color, rgba(255,255,255,0.1))', paddingBottom: '4px' }}>Header Parameters</h3>
              {headerParams.map(p => renderField('header', p.name, p.required, p.description))}
            </div>
          )}

          {bodyFields.length > 0 && (
            <div style={{ marginTop: '8px' }}>
              <h3 style={{ fontSize: '0.8rem', fontWeight: 700, color: 'var(--color-accent, #6366f1)', textTransform: 'uppercase', marginBottom: '10px', borderBottom: '1px solid var(--border-color, rgba(255,255,255,0.1))', paddingBottom: '4px' }}>Request Body Fields</h3>
              {bodyFields.map(f => renderField('body', f.name, f.required, f.description))}
            </div>
          )}

          {operationId && operationDetail && hasNoParams && (
            <p style={{ fontSize: '0.8rem', color: 'var(--text-secondary, #94a3b8)', marginTop: '8px' }}>
              This operation has no configurable parameters.
            </p>
          )}
        </>
      )}
    </div>
  );
}
