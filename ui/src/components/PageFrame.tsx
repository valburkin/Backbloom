import type { ReactNode } from 'react';
import { Eye, Loader2, RotateCcw, Save, TriangleAlert } from 'lucide-react';
import { useConnection } from '../app/connection';
import { useConfig, type PageSettings } from '../app/config';
import { Button, cx } from './ui';
import { PageHeader } from './PageHeader';

/**
 * Standard page: header, scrolling content and — for settings pages — the unsaved-changes bar and the
 * sign-in / loading states. Content is only rendered once the page's settings are available.
 */
export function PageFrame({
  title,
  subtitle,
  actions,
  settings,
  onDiscard,
  wide,
  toolbar,
  unsavedLabel,
  gate = true,
  children,
}: {
  title: string;
  subtitle?: ReactNode;
  actions?: ReactNode;
  settings?: PageSettings;
  /** Extra work when the user discards (e.g. rolling back a live preview). Runs before the draft is dropped. */
  onDiscard?: () => void;
  wide?: boolean;
  /** Shown above the content even while settings are loading or hidden (e.g. tabs). */
  toolbar?: ReactNode;
  /** Text of the unsaved-changes bar, when "You have unsaved changes" would be ambiguous. */
  unsavedLabel?: string;
  /** Hide the content until the settings are loaded (and ask read-only visitors to sign in). */
  gate?: boolean;
  children: ReactNode;
}) {
  return (
    <div className="relative flex h-full flex-col">
      <PageHeader title={title} subtitle={subtitle} actions={actions} />
      <div className="scroll-thin min-h-0 flex-1 overflow-y-auto">
        <div className={cx('mx-auto space-y-6 p-4 sm:p-6', wide ? 'max-w-6xl' : 'max-w-4xl', settings?.dirty && 'pb-28')}>
          {toolbar}
          {settings && gate ? <SettingsGate settings={settings}>{children}</SettingsGate> : children}
        </div>
      </div>
      {settings && <UnsavedBar settings={settings} onDiscard={onDiscard} label={unsavedLabel} />}
    </div>
  );
}

function SettingsGate({ settings, children }: { settings: PageSettings; children: ReactNode }) {
  const { auth, requestLogin } = useConnection();
  const { status, error, reload } = useConfig();
  if (auth === 'readOnly')
    return (
      <div className="flex flex-col items-center gap-3 rounded-2xl border border-line bg-surface p-10 text-center">
        <Eye size={26} className="text-accent-strong" />
        <p className="font-medium">Sign in to see and change these settings</p>
        <p className="max-w-sm text-[13px] text-ink-muted">You’re looking around without signing in, so settings stay hidden.</p>
        <Button variant="primary" onClick={requestLogin}>
          Sign in
        </Button>
      </div>
    );
  if (status === 'error')
    return (
      <div className="flex flex-wrap items-center gap-3 rounded-xl border border-bad/30 bg-bad/10 p-4 text-[13px] text-bad">
        Couldn’t load the settings: {error}
        <Button size="sm" icon={<RotateCcw size={14} />} className="ml-auto" onClick={reload}>
          Try again
        </Button>
      </div>
    );
  if (!settings.ready)
    return (
      <p className="flex items-center justify-center gap-2 p-10 text-sm text-ink-muted">
        <Loader2 size={16} className="animate-spin" /> Loading settings…
      </p>
    );
  return <>{children}</>;
}

function UnsavedBar({ settings, onDiscard, label }: { settings: PageSettings; onDiscard?: () => void; label?: string }) {
  if (!settings.dirty) return null;
  const problems = settings.issues.length;
  return (
    <div className="pointer-events-none absolute inset-x-0 bottom-4 z-20 flex justify-center px-4 sm:bottom-6">
      <div
        role="region"
        aria-label="Unsaved changes"
        className="pointer-events-auto flex w-full max-w-2xl flex-wrap items-center gap-3 rounded-2xl border border-line-strong bg-surface-2/95 px-4 py-3 shadow-2xl backdrop-blur"
      >
        <span className="h-2 w-2 shrink-0 rounded-full bg-warn" aria-hidden />
        <p className="min-w-0 flex-1 text-[13px]">
          {problems ? (
            <span className="inline-flex items-center gap-1.5 text-bad">
              <TriangleAlert size={14} /> {problems === 1 ? 'One value needs attention' : `${problems} values need attention`}
            </span>
          ) : (
            (label ?? 'You have unsaved changes')
          )}
        </p>
        <Button
          size="sm"
          variant="ghost"
          disabled={settings.saving}
          onClick={() => {
            onDiscard?.();
            settings.discard();
          }}
        >
          Discard
        </Button>
        <Button
          size="sm"
          variant="primary"
          disabled={settings.saving || problems > 0 || !settings.canEdit}
          icon={settings.saving ? <Loader2 size={15} className="animate-spin" /> : <Save size={15} />}
          onClick={() => void settings.save()}
        >
          {settings.saving ? 'Saving…' : 'Save'}
        </Button>
      </div>
    </div>
  );
}
