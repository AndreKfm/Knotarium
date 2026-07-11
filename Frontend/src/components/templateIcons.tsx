import type { SVGProps } from 'react';

// Feather-style inline icons for the Templates screen (stroke 2, round caps/joins),
// mirroring the design handoff's TI map. Kept local so the redesign is self-contained.
type IconProps = SVGProps<SVGSVGElement>;

const base = (size: number, props: IconProps) => ({
  width: size, height: size, viewBox: '0 0 24 24', fill: 'none',
  stroke: 'currentColor', strokeWidth: 2, strokeLinecap: 'round' as const,
  strokeLinejoin: 'round' as const, ...props,
});

export const TI = {
  grid: (p: IconProps = {}) => <svg {...base(16, p)}><rect x="3" y="3" width="7" height="7" rx="1" /><rect x="14" y="3" width="7" height="7" rx="1" /><rect x="3" y="14" width="7" height="7" rx="1" /><rect x="14" y="14" width="7" height="7" rx="1" /></svg>,
  download: (p: IconProps = {}) => <svg {...base(17, p)}><path d="M21 15v4a2 2 0 0 1-2 2H5a2 2 0 0 1-2-2v-4" /><path d="M7 10l5 5 5-5" /><path d="M12 15V3" /></svg>,
  upload: (p: IconProps = {}) => <svg {...base(17, p)}><path d="M21 15v4a2 2 0 0 1-2 2H5a2 2 0 0 1-2-2v-4" /><path d="M17 8l-5-5-5 5" /><path d="M12 3v12" /></svg>,
  check: (p: IconProps = {}) => <svg {...base(14, p)} strokeWidth={2.6}><path d="M20 6 9 17l-5-5" /></svg>,
  x: (p: IconProps = {}) => <svg {...base(15, p)} strokeWidth={2.2}><path d="M18 6 6 18M6 6l12 12" /></svg>,
  file: (p: IconProps = {}) => <svg {...base(18, p)}><path d="M14 2H6a2 2 0 0 0-2 2v16a2 2 0 0 0 2 2h12a2 2 0 0 0 2-2V8z" /><path d="M14 2v6h6" /></svg>,
  shield: (p: IconProps = {}) => <svg {...base(16, p)}><path d="M20 13c0 5-3.5 7.5-7.66 8.95a1 1 0 0 1-.67-.01C7.5 20.5 4 18 4 13V6a1 1 0 0 1 1-1c2 0 4.5-1.2 6.24-2.72a1.17 1.17 0 0 1 1.52 0C14.51 3.81 17 5 19 5a1 1 0 0 1 1 1Z" /></svg>,
  key: (p: IconProps = {}) => <svg {...base(14, p)}><circle cx="7.5" cy="15.5" r="5.5" /><path d="m21 2-9.6 9.6M15.5 7.5l3 3L22 7l-3-3" /></svg>,
  search: (p: IconProps = {}) => <svg {...base(17, p)}><circle cx="11" cy="11" r="7" /><path d="m21 21-4.3-4.3" /></svg>,
  chev: (p: IconProps = {}) => <svg {...base(16, p)}><path d="m6 9 6 6 6-6" /></svg>,
  spin: (p: IconProps = {}) => <svg {...base(16, p)} strokeWidth={2.4} strokeLinejoin={undefined}><path d="M21 12a9 9 0 1 1-6.2-8.56" /></svg>,
  node: (p: IconProps = {}) => <svg {...base(13, p)}><circle cx="6" cy="6" r="3" /><circle cx="18" cy="18" r="3" /><path d="M9 6h6a3 3 0 0 1 3 3v6" /></svg>,
  plug: (p: IconProps = {}) => <svg {...base(14, p)}><path d="M12 22v-5M9 8V2M15 8V2M18 8v3a6 6 0 0 1-12 0V8Z" /></svg>,
  sparkle: (p: IconProps = {}) => <svg {...base(20, p)}><path d="M12 3l1.9 5.1L19 10l-5.1 1.9L12 17l-1.9-5.1L5 10l5.1-1.9Z" /><path d="M19 15l.7 1.8L21.5 18l-1.8.7L19 20.5l-.7-1.8L16.5 18l1.8-.7Z" /></svg>,
  clock: (p: IconProps = {}) => <svg {...base(20, p)}><circle cx="12" cy="12" r="9" /><path d="M12 7v5l3 2" /></svg>,
  globe: (p: IconProps = {}) => <svg {...base(20, p)}><circle cx="12" cy="12" r="9" /><path d="M3 12h18M12 3a14 14 0 0 1 0 18 14 14 0 0 1 0-18Z" /></svg>,
  refresh: (p: IconProps = {}) => <svg {...base(20, p)}><path d="M3 12a9 9 0 0 1 15-6.7L21 8" /><path d="M21 3v5h-5" /><path d="M21 12a9 9 0 0 1-15 6.7L3 16" /><path d="M3 21v-5h5" /></svg>,
  layout: (p: IconProps = {}) => <svg {...base(20, p)}><rect x="3" y="3" width="18" height="18" rx="2" /><path d="M3 9h18M9 21V9" /></svg>,
  info: (p: IconProps = {}) => <svg {...base(16, p)}><circle cx="12" cy="12" r="9" /><path d="M12 16v-4M12 8h.01" /></svg>,
  sliders: (p: IconProps = {}) => <svg {...base(14, p)}><path d="M4 21v-7M4 10V3M12 21v-9M12 8V3M20 21v-5M20 12V3M1 14h6M9 8h6M17 16h6" /></svg>,
  eye: (p: IconProps = {}) => <svg {...base(16, p)}><path d="M2 12s3.5-7 10-7 10 7 10 7-3.5 7-10 7-10-7-10-7Z" /><circle cx="12" cy="12" r="3" /></svg>,
  trash: (p: IconProps = {}) => <svg {...base(15, p)}><path d="M3 6h18M8 6V4a1 1 0 0 1 1-1h6a1 1 0 0 1 1 1v2M19 6l-1 14a2 2 0 0 1-2 2H8a2 2 0 0 1-2-2L5 6" /></svg>,
};

// Category → icon + tone, so gallery cards get visual variety from the manifest's category.
export function categoryVisual(category: string): { icon: keyof typeof TI; tone: string } {
  switch ((category || '').toLowerCase()) {
    case 'integration': return { icon: 'globe', tone: 'cyan' };
    case 'scheduling': return { icon: 'clock', tone: 'amber' };
    case 'pattern': return { icon: 'refresh', tone: 'teal' };
    case 'starter': return { icon: 'sparkle', tone: '' };
    default: return { icon: 'layout', tone: '' };
  }
}
