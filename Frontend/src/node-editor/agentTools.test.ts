import { describe, it, expect } from 'vitest';
import {
  isValidToolName,
  readToolBindings,
  validateToolBindings,
  type AgentToolBinding,
} from './agentTools';

describe('agentTools', () => {
  describe('isValidToolName', () => {
    it('accepts letters, digits, underscore up to 64 chars', () => {
      expect(isValidToolName('lookup_customer')).toBe(true);
      expect(isValidToolName('Tool1')).toBe(true);
    });
    it('rejects spaces, empty, and over-long names', () => {
      expect(isValidToolName('has space')).toBe(false);
      expect(isValidToolName('')).toBe(false);
      expect(isValidToolName('a'.repeat(65))).toBe(false);
    });
  });

  describe('readToolBindings', () => {
    it('reads a live array and normalizes parameters + outputs', () => {
      const bindings = readToolBindings([
        {
          workflowId: 'wf-1',
          name: 'lookup',
          description: 'd',
          parameters: [{ name: 'id', type: 'integer', required: true }],
          outputs: ['customer', '', '  found  '],
        },
      ]);
      expect(bindings).toHaveLength(1);
      expect(bindings[0].parameters[0]).toEqual({ name: 'id', type: 'number', required: true, description: undefined });
      // Empty output dropped, whitespace trimmed.
      expect(bindings[0].outputs).toEqual(['customer', 'found']);
    });

    it('parses a legacy JSON string', () => {
      const bindings = readToolBindings('[{"workflowId":"w","name":"t","description":"","parameters":[],"outputs":[]}]');
      expect(bindings).toHaveLength(1);
      expect(bindings[0].name).toBe('t');
    });

    it('returns empty for junk / non-array / bad JSON', () => {
      expect(readToolBindings(null)).toEqual([]);
      expect(readToolBindings('not json')).toEqual([]);
      expect(readToolBindings({ not: 'an array' })).toEqual([]);
      expect(readToolBindings('')).toEqual([]);
    });

    it('drops non-object parameter entries but keeps in-progress empty-named rows', () => {
      const bindings = readToolBindings([
        { workflowId: 'w', name: 't', parameters: ['junk', { type: 'string' }, { name: 'ok', type: 'string' }] },
      ]);
      // 'junk' (non-object) dropped; the empty-named object kept as an editable placeholder.
      expect(bindings[0].parameters).toHaveLength(2);
      expect(bindings[0].parameters.map((p) => p.name)).toEqual(['', 'ok']);
    });
  });

  describe('validateToolBindings', () => {
    const base = (over: Partial<AgentToolBinding>): AgentToolBinding => ({
      workflowId: 'w', name: 'ok', description: '', parameters: [], outputs: [], ...over,
    });

    it('accepts a valid list', () => {
      expect(validateToolBindings([base({ name: 'a' }), base({ name: 'b' })])).toEqual([]);
    });

    it('flags invalid names, duplicates, and missing workflow', () => {
      const problems = validateToolBindings([
        base({ name: 'bad name' }),
        base({ name: 'dup' }),
        base({ name: 'dup' }),
        base({ name: 'nowf', workflowId: '' }),
      ]);
      expect(problems.some((p) => p.includes('invalid'))).toBe(true);
      expect(problems.some((p) => p.includes('Duplicate'))).toBe(true);
      expect(problems.some((p) => p.includes('no target workflow'))).toBe(true);
    });
  });
});
