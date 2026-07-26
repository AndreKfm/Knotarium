// Copyright 2026 Andre Kaufmann
// SPDX-License-Identifier: Apache-2.0

import { useEffect, useLayoutEffect, useRef, useState, type ReactNode } from 'react';
import { createPortal } from 'react-dom';
import { ChevronDown, Compass, LogOut, UserCircle } from 'lucide-react';
import { useAuth } from './AuthContext';

interface UserMenuProps {
  /** Re-open the guided product tour. */
  onOpenTour: () => void;
  /** Local wall clock; shown here because the bar sheds its own clock first. */
  localTime: ReactNode;
}

/**
 * Account button in the top bar: the signed-in user collapsed into a single
 * 34px target, with the things that are ACTIONS rather than destinations
 * (tour, sign out) moved off the bar and into its menu — they were costing
 * width that Dashboard and Execution Visualizer need.
 *
 * The menu is portaled to <body>: the bar clips its own children, so a menu
 * rendered inside it would be cut off at the first pixel below the bar.
 */
export function UserMenu({ onOpenTour, localTime }: UserMenuProps) {
  const { status, logout } = useAuth();
  const [open, setOpen] = useState(false);
  const [position, setPosition] = useState<{ top: number; right: number } | null>(null);
  const buttonRef = useRef<HTMLButtonElement | null>(null);
  const menuRef = useRef<HTMLDivElement | null>(null);

  useLayoutEffect(() => {
    if (!open) return;
    const rect = buttonRef.current?.getBoundingClientRect();
    if (!rect) return;
    // Hung off the bar's bottom edge rather than the button's, so the menu sits
    // below the bar instead of overlapping its lower few pixels.
    const barBottom = buttonRef.current?.closest('.tb')?.getBoundingClientRect().bottom ?? rect.bottom;
    setPosition({ top: barBottom + 8, right: Math.max(8, window.innerWidth - rect.right) });
  }, [open]);

  useEffect(() => {
    if (!open) return;
    const onPointerDown = (event: MouseEvent) => {
      const target = event.target as Node;
      if (menuRef.current?.contains(target) || buttonRef.current?.contains(target)) return;
      setOpen(false);
    };
    const onKey = (event: KeyboardEvent) => { if (event.key === 'Escape') setOpen(false); };
    // A window resize (or scroll) invalidates the anchored position; closing is
    // less surprising than a menu that drifts away from its button.
    const onReflow = () => setOpen(false);
    window.addEventListener('mousedown', onPointerDown);
    window.addEventListener('keydown', onKey, true);
    window.addEventListener('resize', onReflow);
    return () => {
      window.removeEventListener('mousedown', onPointerDown);
      window.removeEventListener('keydown', onKey, true);
      window.removeEventListener('resize', onReflow);
    };
  }, [open]);

  if (!status?.authenticated) {
    return null;
  }

  const choose = (action: () => void) => {
    setOpen(false);
    action();
  };

  return (
    <>
      <button
        type="button"
        ref={buttonRef}
        className="tb-gbtn"
        onClick={() => setOpen((v) => !v)}
        aria-haspopup="menu"
        aria-expanded={open}
        aria-label={`Account: ${status.username ?? 'signed in'}`}
        title={status.username ?? 'Account'}
      >
        <UserCircle size={16} />
        <ChevronDown size={13} />
      </button>
      {open && position && createPortal(
        <div className="tb-menu" role="menu" ref={menuRef} style={{ top: position.top, right: position.right }}>
          <div className="tb-menu-head">
            <UserCircle size={16} /> {status.username}
          </div>
          <button type="button" role="menuitem" className="tb-menu-item" onClick={() => choose(onOpenTour)}>
            <Compass size={15} /> Product tour
          </button>
          <button type="button" role="menuitem" className="tb-menu-item" onClick={() => choose(() => void logout())}>
            <LogOut size={15} /> Sign out
          </button>
          <div className="tb-menu-foot">Local time {localTime}</div>
        </div>,
        document.body,
      )}
    </>
  );
}
