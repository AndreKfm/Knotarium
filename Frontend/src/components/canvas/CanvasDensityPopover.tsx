// Copyright 2026 Andre Kaufmann
// SPDX-License-Identifier: Apache-2.0

import type { Dispatch, RefObject, SetStateAction } from 'react'
import { Eye } from 'lucide-react'

export type DensityMode = 'reveal' | 'dots' | 'boxes'

const DENSITY_OPTIONS: { mode: DensityMode; label: string; desc: string }[] = [
  { mode: 'reveal', label: 'Reveal on demand', desc: 'Canvas shows execution flow only. Hover/click nodes to trace.' },
  { mode: 'dots', label: 'Compact dots', desc: 'Dashed wires always on, with collapsed midpoint diamonds.' },
  { mode: 'boxes', label: 'Always-on value boxes', desc: 'Dashed wires and value tokens always visible.' },
]

export interface CanvasDensityPopoverProps {
  popoverRef: RefObject<HTMLDivElement | null>
  isOpen: boolean
  setIsOpen: Dispatch<SetStateAction<boolean>>
  densityMode: DensityMode
  setDensityMode: (mode: DensityMode) => void
}

/**
 * Data-wire density picker: a floating trigger button (bottom-right) that opens a popover to switch
 * between reveal-on-demand / compact-dots / always-on-value-boxes. Presentational; extracted from
 * Canvas.tsx.
 */
export function CanvasDensityPopover({ popoverRef, isOpen, setIsOpen, densityMode, setDensityMode }: CanvasDensityPopoverProps) {
  return (
    <div ref={popoverRef} style={{ position: 'absolute', bottom: '16px', right: '90px', zIndex: 1000, display: 'flex', flexDirection: 'column', alignItems: 'flex-end' }}>
      {isOpen && (
        <div
          style={{
            background: 'rgba(16, 22, 37, 0.95)',
            backdropFilter: 'blur(12px)',
            border: '1px solid var(--border-color)',
            borderRadius: '10px',
            padding: '12px 16px',
            width: '240px',
            boxShadow: '0 10px 25px -5px rgba(0, 0, 0, 0.7)',
            display: 'flex',
            flexDirection: 'column',
            gap: '10px',
            marginBottom: '8px',
          }}
        >
          <span style={{ fontSize: '0.75rem', fontWeight: 700, color: 'var(--text-secondary)', textTransform: 'uppercase', letterSpacing: '0.05em' }}>
            Data Wire Density
          </span>
          <div style={{ display: 'flex', flexDirection: 'column', gap: '6px' }}>
            {DENSITY_OPTIONS.map((opt) => (
              <button
                key={opt.mode}
                onClick={() => {
                  setDensityMode(opt.mode)
                  setIsOpen(false)
                }}
                style={{
                  background: densityMode === opt.mode ? 'rgba(99, 102, 241, 0.15)' : 'transparent',
                  border: densityMode === opt.mode ? '1px solid var(--color-accent)' : '1px solid transparent',
                  borderRadius: '6px',
                  padding: '8px 10px',
                  textAlign: 'left',
                  cursor: 'pointer',
                  color: '#fff',
                  transition: 'all 0.15s ease',
                  display: 'flex',
                  flexDirection: 'column',
                  gap: '2px',
                }}
                onMouseOver={(e) => {
                  if (densityMode !== opt.mode) {
                    e.currentTarget.style.background = 'rgba(255, 255, 255, 0.03)'
                  }
                }}
                onMouseOut={(e) => {
                  if (densityMode !== opt.mode) {
                    e.currentTarget.style.background = 'transparent'
                  }
                }}
              >
                <span style={{ fontSize: '0.8rem', fontWeight: 700, color: densityMode === opt.mode ? 'var(--color-accent)' : '#fff' }}>
                  {opt.label}
                </span>
                <span style={{ fontSize: '0.65rem', color: 'var(--text-muted)', lineHeight: '1.25' }}>
                  {opt.desc}
                </span>
              </button>
            ))}
          </div>
        </div>
      )}

      <button
        onClick={() => setIsOpen(!isOpen)}
        title="Data Wire Density Settings"
        style={{
          background: 'rgba(16, 22, 37, 0.85)',
          backdropFilter: 'blur(10px)',
          border: '1px solid var(--border-color)',
          color: '#fff',
          borderRadius: '8px',
          width: '38px',
          height: '38px',
          display: 'flex',
          alignItems: 'center',
          justifyContent: 'center',
          cursor: 'pointer',
          boxShadow: '0 4px 12px rgba(0, 0, 0, 0.5)',
          transition: 'background 0.2s, border-color 0.2s',
        }}
        onMouseOver={(e) => {
          e.currentTarget.style.background = 'rgba(16, 22, 37, 0.95)'
          e.currentTarget.style.borderColor = 'rgba(255, 255, 255, 0.15)'
        }}
        onMouseOut={(e) => {
          e.currentTarget.style.background = 'rgba(16, 22, 37, 0.85)'
          e.currentTarget.style.borderColor = 'var(--border-color)'
        }}
      >
        <Eye size={18} color={densityMode !== 'reveal' ? 'var(--color-accent)' : '#fff'} />
      </button>
    </div>
  )
}
