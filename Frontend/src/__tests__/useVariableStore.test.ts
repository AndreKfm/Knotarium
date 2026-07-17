// Copyright 2026 Andre Kaufmann
// SPDX-License-Identifier: Apache-2.0

import { beforeEach, describe, expect, it, vi } from 'vitest';
import { useVariableStore } from '../stores/useVariableStore';
import type { ExecutionInstance } from '../types';

describe('useVariableStore', () => {
  const workflowId = 'test-workflow-id';

  beforeEach(() => {
    localStorage.clear();
    useVariableStore.getState().resetStore();
    useVariableStore.persist.clearStorage();
    vi.useFakeTimers();
  });

  it('addVariable adds variable and enforces unique names', () => {
    const { addVariable } = useVariableStore.getState();

    // Add first variable
    const success1 = addVariable(workflowId, {
      name: 'counter',
      type: 'number',
      producer: 'node-1',
      producerOutput: 'success',
      value: 10,
    });
    expect(success1).not.toBeNull();
    expect(useVariableStore.getState().variables[workflowId]).toHaveLength(1);
    expect(useVariableStore.getState().variables[workflowId][0].name).toBe('counter');
    expect(useVariableStore.getState().variables[workflowId][0].producerOutput).toBe('success');

    // Add duplicate name variable (case insensitive)
    const success2 = addVariable(workflowId, {
      name: 'COUNTER',
      type: 'string',
      producer: 'node-2',
      producerOutput: 'success',
      value: 'hello',
    });
    expect(success2).toBeNull();
    expect(useVariableStore.getState().variables[workflowId]).toHaveLength(1);
    expect(useVariableStore.getState().conflictingName).toBe('COUNTER');

    // Wait for conflict timeout to expire
    vi.advanceTimersByTime(1000);
    expect(useVariableStore.getState().conflictingName).toBeNull();
  });

  it('addVariable returns existing variable if producer and producerOutput match', () => {
    const { addVariable } = useVariableStore.getState();

    const var1 = addVariable(workflowId, {
      name: 'counter',
      type: 'number',
      producer: 'node-1',
      producerOutput: 'success',
      value: 10,
    });
    expect(var1).not.toBeNull();

    const var2 = addVariable(workflowId, {
      name: 'another_name',
      type: 'number',
      producer: 'node-1',
      producerOutput: 'success',
      value: 10,
    });
    expect(var2).toEqual(var1);
    expect(useVariableStore.getState().variables[workflowId]).toHaveLength(1);
  });

  it('removeVariable deletes variable', () => {
    const { addVariable, removeVariable } = useVariableStore.getState();

    addVariable(workflowId, {
      name: 'foo',
      type: 'string',
      producer: 'node-1',
      producerOutput: 'success',
      value: 'bar',
    });
    const variable = useVariableStore.getState().variables[workflowId][0];

    removeVariable(workflowId, variable.id);
    expect(useVariableStore.getState().variables[workflowId]).toEqual([]);
  });

  it('renameVariable renames variable and prevents duplicates', () => {
    const { addVariable, renameVariable } = useVariableStore.getState();

    addVariable(workflowId, {
      name: 'var1',
      type: 'string',
      producer: 'node-1',
      producerOutput: 'success',
      value: 'val1',
    });
    addVariable(workflowId, {
      name: 'var2',
      type: 'number',
      producer: 'node-2',
      producerOutput: 'success',
      value: 123,
    });

    const v1 = useVariableStore.getState().variables[workflowId].find(v => v.name === 'var1')!;
    const v2 = useVariableStore.getState().variables[workflowId].find(v => v.name === 'var2')!;

    // Successful rename
    const successRename = renameVariable(workflowId, v1.id, 'var3');
    expect(successRename).toBe(true);
    expect(useVariableStore.getState().variables[workflowId].find(v => v.id === v1.id)!.name).toBe('var3');

    // Unsuccessful rename (duplicate)
    const failRename = renameVariable(workflowId, v2.id, 'var3');
    expect(failRename).toBe(false);
    expect(useVariableStore.getState().variables[workflowId].find(v => v.id === v2.id)!.name).toBe('var2');
    expect(useVariableStore.getState().conflictingName).toBe('var3');
  });

  it('syncConsumers derives consumers correctly from nodes properties', () => {
    const { addVariable, syncConsumers } = useVariableStore.getState();

    addVariable(workflowId, {
      name: 'user_name',
      type: 'string',
      producer: 'node-1',
      producerOutput: 'success',
      value: 'admin',
    });
    const variable = useVariableStore.getState().variables[workflowId][0];

    // Mock nodes referencing the variable
    const mockNodes = [
      {
        id: 'consumer-node-1',
        data: {
          properties: {
            message: {
              __type: 'variable_ref',
              variableId: variable.id,
              variableName: 'user_name',
            },
          },
        },
      },
      {
        id: 'non-consumer-node',
        data: {
          properties: {
            message: 'hello world',
          },
        },
      },
    ];

    syncConsumers(workflowId, mockNodes);
    expect(useVariableStore.getState().variables[workflowId][0].consumers).toEqual(['consumer-node-1']);
  });

  it('syncConsumers is a no-op (preserves the variables reference) when consumers are unchanged', () => {
    // Perf-critical: this runs on every node-drag frame, and every node card subscribes to
    // variables[workflowId]. A fresh array on each call re-rendered all cards every frame (drag jank).
    // A position-only drag never changes consumers, so a repeat sync must keep the same reference.
    const { addVariable, syncConsumers } = useVariableStore.getState();

    addVariable(workflowId, {
      name: 'user_name', type: 'string', producer: 'node-1', producerOutput: 'success', value: 'admin',
    });
    const variable = useVariableStore.getState().variables[workflowId][0];
    const mockNodes = [
      { id: 'consumer-node-1', data: { properties: { message: { __type: 'variable_ref', variableId: variable.id, variableName: 'user_name' } } } },
      { id: 'other-node', data: { properties: { message: 'plain' } } },
    ];

    syncConsumers(workflowId, mockNodes);
    const refAfterFirst = useVariableStore.getState().variables[workflowId];

    // Re-sync with the same node graph (what each subsequent drag frame does) → same reference, no churn.
    syncConsumers(workflowId, mockNodes);
    expect(useVariableStore.getState().variables[workflowId]).toBe(refAfterFirst);

    // A genuine consumer change still produces a new reference (so subscribers update).
    syncConsumers(workflowId, [...mockNodes, { id: 'consumer-node-2', data: { properties: { x: { __type: 'variable_ref', variableId: variable.id } } } }]);
    expect(useVariableStore.getState().variables[workflowId]).not.toBe(refAfterFirst);
    expect(useVariableStore.getState().variables[workflowId][0].consumers).toEqual(['consumer-node-1', 'consumer-node-2']);
  });

  it('re-deriving a keyed Set Variable does not wipe a previously resolved value', () => {
    const { syncDeclaredVariables, updateVariableValues } = useVariableStore.getState();

    // Initial derivation + a run that resolves myDict to an object.
    syncDeclaredVariables(workflowId, [
      { producer: 'sv-1', name: 'myDict', type: 'object', value: { name: 'Alice' } },
    ]);
    updateVariableValues(workflowId, {
      id: 'exec-1',
      workflowDefinitionId: { value: workflowId },
      status: 'Completed',
      createdAt: '', updatedAt: '',
      globalVariables: { myDict: { name: 'Alice' } },
      nodeStates: [],
    } as unknown as ExecutionInstance);

    let myDict = useVariableStore.getState().variables[workflowId].find(v => v.name === 'myDict')!;
    expect(myDict.status).toBe('resolved');
    expect(myDict.value).toEqual({ name: 'Alice' });

    // A keyed write (myDict["name"]) re-derives the head with no design-time value.
    // The previously resolved value must survive rather than being clobbered to undefined.
    syncDeclaredVariables(workflowId, [
      { producer: 'sv-1', name: 'myDict', type: 'object', value: undefined },
    ]);

    myDict = useVariableStore.getState().variables[workflowId].find(v => v.name === 'myDict')!;
    expect(myDict.value).toEqual({ name: 'Alice' });
  });

  it('updateVariableValues and clearVariableValues manage execution value states', () => {
    const { addVariable, updateVariableValues, clearVariableValues } = useVariableStore.getState();

    addVariable(workflowId, {
      name: 'http_response_body',
      type: 'object',
      producer: 'httpRequest-1',
      producerOutput: 'body',
      value: null,
    });
    addVariable(workflowId, {
      name: 'global_counter',
      type: 'number',
      producer: 'setVariable-1',
      producerOutput: 'success',
      value: 0,
    });

    // Simulate execution update
    const mockExecution = {
      id: 'exec-1',
      workflowDefinitionId: { value: workflowId },
      status: 'Completed',
      createdAt: '',
      updatedAt: '',
      globalVariables: {
        global_counter: 99,
      },
      nodeStates: [
        {
          id: 'ns-1',
          executionInstanceId: 'exec-1',
          nodeId: { value: 'httpRequest-1' },
          status: 'Completed',
          inputs: {},
          outputs: {
            body: { message: 'Success' },
            statusCode: 200,
          },
          executionCount: 1,
        }
      ]
    } as unknown as ExecutionInstance;

    updateVariableValues(workflowId, mockExecution);

    const variables = useVariableStore.getState().variables[workflowId];
    const bodyVar = variables.find(v => v.name === 'http_response_body')!;
    const counterVar = variables.find(v => v.name === 'global_counter')!;

    expect(bodyVar.value).toEqual({ message: 'Success' });
    expect(bodyVar.status).toBe('resolved');

    expect(counterVar.value).toBe(99);
    expect(counterVar.status).toBe('resolved');

    // Simulate clearing
    clearVariableValues(workflowId);
    const cleared = useVariableStore.getState().variables[workflowId].find(v => v.name === 'http_response_body')!;
    expect(cleared.value).toBeUndefined();
    expect(cleared.status).toBe('awaiting run');
  });

  it('setDraggingToken manages isDraggingToken and draggedToken state', () => {
    const { setDraggingToken } = useVariableStore.getState();

    // Start dragging token
    setDraggingToken(true, {
      variableId: 'var-123',
      variableName: 'test_var',
      type: 'string',
    });

    expect(useVariableStore.getState().isDraggingToken).toBe(true);
    expect(useVariableStore.getState().draggedToken).toEqual({
      variableId: 'var-123',
      variableName: 'test_var',
      type: 'string',
    });

    // Stop dragging token
    setDraggingToken(false, null);
    expect(useVariableStore.getState().isDraggingToken).toBe(false);
    expect(useVariableStore.getState().draggedToken).toBeNull();
  });

  it('manages densityMode state', () => {
    const { setDensityMode } = useVariableStore.getState();
    expect(useVariableStore.getState().densityMode).toBe('reveal');

    setDensityMode('dots');
    expect(useVariableStore.getState().densityMode).toBe('dots');

    setDensityMode('boxes');
    expect(useVariableStore.getState().densityMode).toBe('boxes');
  });

  it('manages hover states', () => {
    const { setHoveredNodeId, setHoveredVariableId } = useVariableStore.getState();
    expect(useVariableStore.getState().hoveredNodeId).toBeNull();
    expect(useVariableStore.getState().hoveredVariableId).toBeNull();

    setHoveredNodeId('node-1');
    expect(useVariableStore.getState().hoveredNodeId).toBe('node-1');

    setHoveredVariableId('var-1');
    expect(useVariableStore.getState().hoveredVariableId).toBe('var-1');
  });

  it('manages accumulating pin states and clearPins', () => {
    const { togglePinnedNodeId, togglePinnedVariableId, clearPins } = useVariableStore.getState();
    expect(useVariableStore.getState().pinnedNodeIds).toEqual([]);
    expect(useVariableStore.getState().pinnedVariableIds).toEqual([]);

    togglePinnedNodeId('node-1');
    expect(useVariableStore.getState().pinnedNodeIds).toEqual(['node-1']);

    togglePinnedNodeId('node-2');
    expect(useVariableStore.getState().pinnedNodeIds).toEqual(['node-1', 'node-2']);

    // Toggle off node-1
    togglePinnedNodeId('node-1');
    expect(useVariableStore.getState().pinnedNodeIds).toEqual(['node-2']);

    togglePinnedVariableId('var-1');
    expect(useVariableStore.getState().pinnedVariableIds).toEqual(['var-1']);

    togglePinnedVariableId('var-2');
    expect(useVariableStore.getState().pinnedVariableIds).toEqual(['var-1', 'var-2']);

    clearPins();
    expect(useVariableStore.getState().pinnedNodeIds).toEqual([]);
    expect(useVariableStore.getState().pinnedVariableIds).toEqual([]);
  });
});
