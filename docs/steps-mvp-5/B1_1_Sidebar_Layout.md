# Step 14 — B1.1: Sidebar Palette Layout

## Goal
Transition the frontend `Canvas.tsx` by retiring the horizontal canvas floating toolbar and replacing it with a persistent, left-docked `SidebarPalette.tsx` component. The sidebar must organize all available node packages into collapsible categories based on their manifest `category` property, sorting them alphabetically, with fuzzy-search filtering pinned at the top.

---

## Invariant Alignment
* **Invariant 1.1 (Sidebar Authority):** The sidebar must be persistent and left-docked, serving as the sole canvas insertion mechanic.
* **Invariant 1.2 (Fuzzy Search):** A search input must be pinned at the top, offering client-side text filtering.
* **Invariant 1.3 (Manifest-Driven Grouping):** Available packages must be grouped collapsibly into `Trigger`, `Logic`, `Data`, `Network`, and `Utility` categories, with alphabetical node sorting.

---

## Proposed Changes

### 1. Create [SidebarPalette.tsx](file:///d:/Private/Source/AknSideProjects/Automate/Frontend/src/components/SidebarPalette.tsx) [NEW]
Build the sidebar component with modern dark mode and glassmorphic styling:
```tsx
import React, { useState } from 'react';
import { NodePackageManifest } from '../types';

interface SidebarPaletteProps {
  availableNodes: NodePackageManifest[];
  onDragStart: (event: React.DragEvent, nodeType: string) => void;
}

export const SidebarPalette: React.FC<SidebarPaletteProps> = ({ availableNodes, onDragStart }) => {
  const [searchQuery, setSearchQuery] = useState('');
  const [collapsedCategories, setCollapsedCategories] = useState<Record<string, boolean>>({});

  // Fuzzy filter and manifest grouping logic...
};
```
* **Categories**: Collapsible sections for `Trigger`, `Logic`, `Data`, `Network`, and `Utility`.
* **Fuzzy search**: Low-latency regex or lowercase inclusion check across display names and descriptions.

### 2. Modify [Canvas.tsx](file:///d:/Private/Source/AknSideProjects/Automate/Frontend/src/components/Canvas.tsx) [MODIFY]
* Position the new `<SidebarPalette />` layout element to the left of the React Flow canvas workspace using grid/flexbox.
* **Retire horizontal toolbar**: Delete the legacy floating toolbar element code block (lines 389–428).

---

## Verification & Test Checklist

### 1. Component Tests
* Write a Jest/React Testing Library test in `SidebarPalette.test.tsx` to verify:
  * **Collapsible Groups**: Component renders all categories collapsibly.
  * **Search Filtering**: Typing in the search input correctly filters list items.
  * **Sorting**: Nodes inside a category are sorted alphabetically.

### 2. Manual Verification
* Confirms left-docked layout is persistent and does not overlap node canvas interactions.
