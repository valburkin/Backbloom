import type { ReactNode } from 'react';

/** @param badge Next to the title, e.g. the mode on a page that differs between Ambient and Game. */
export function PageHeader({ title, badge, subtitle, actions }: { title: string; badge?: ReactNode; subtitle?: ReactNode; actions?: ReactNode }) {
  return (
    <header className="flex flex-wrap items-center justify-between gap-3 border-b border-line px-6 py-4">
      <div className="min-w-0">
        <h1 className="flex items-center gap-3 text-2xl font-semibold tracking-tight">
          {title}
          {badge}
        </h1>
        {subtitle && <p className="mt-0.5 text-sm text-ink-muted">{subtitle}</p>}
      </div>
      {actions && <div className="flex flex-wrap items-center gap-2">{actions}</div>}
    </header>
  );
}
