import type { ReactNode } from 'react';
import { Check, Loader2, RotateCcw, Save, TriangleAlert } from 'lucide-react';
import { useConfig, type PageSettings } from '../app/config';
import { Button, cx, FLOATING } from './ui';
import { PageHeader } from './PageHeader';

/**
 * Standard page: header, scrolling content and — for settings pages — the auto-save indicator, the unsaved-changes
 * bar (edits only Save stores, values that need attention, a failed automatic save) and the sign-in / loading states. Content is only rendered once the page's settings are available.
 */
export function PageFrame({
  title,
  badge,
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
  /** Next to the title. */
  badge?: ReactNode;
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
  const bar = !!settings && showsBar(settings);
  const status = settings && <AutoSaveStatus settings={settings} />;
  return (
    <div className="relative flex h-full flex-col">
      <PageHeader
        title={title}
        badge={badge}
        subtitle={subtitle}
        actions={
          status || actions ? (
            <>
              {status}
              {actions}
            </>
          ) : undefined
        }
      />
      <div className="scroll-thin min-h-0 flex-1 overflow-y-auto">
        <div className={cx('mx-auto space-y-6 p-4 sm:px-8 sm:pb-8 sm:pt-4', wide ? 'max-w-6xl' : 'max-w-3xl', bar && 'pb-28 sm:pb-28')}>
          {toolbar}
          {settings && gate ? <SettingsGate settings={settings}>{children}</SettingsGate> : children}
        </div>
      </div>
      {settings && <UnsavedBar settings={settings} onDiscard={onDiscard} label={unsavedLabel} />}
    </div>
  );
}

function SettingsGate({ settings, children }: { settings: PageSettings; children: ReactNode }) {
  const { status, error, reload } = useConfig();
  if (status === 'error')
    return (
      <div className="flex flex-wrap items-center gap-3 rounded-2xl border border-bad/30 bg-bad/10 p-4 text-[13px] text-bad">
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

function showsBar(settings: PageSettings): boolean {
  return settings.dirty && (settings.needsSave || settings.issues.length > 0 || settings.autoSave === 'failed');
}

/** "Saving…" / "Saved" for pages whose changes are stored automatically. */
function AutoSaveStatus({ settings }: { settings: PageSettings }) {
  const s = settings.autoSave;
  if (s !== 'pending' && s !== 'saving' && s !== 'saved') return null;
  return (
    <span className="inline-flex items-center gap-1.5 text-[13px] text-ink-faint" aria-live="polite">
      {s === 'saved' ? <Check size={14} className="text-ok" /> : <Loader2 size={14} className="animate-spin" />}
      {s === 'saved' ? 'Saved' : 'Saving…'}
    </span>
  );
}

function UnsavedBar({ settings, onDiscard, label }: { settings: PageSettings; onDiscard?: () => void; label?: string }) {
  if (!showsBar(settings)) return null;
  const problems = settings.issues.length;
  return (
    <div className="pointer-events-none absolute inset-x-0 bottom-4 z-20 flex justify-center px-4 sm:bottom-6">
      <div
        role="region"
        aria-label="Unsaved changes"
        className={cx('pointer-events-auto flex w-full max-w-2xl flex-wrap items-center gap-3 rounded-[28px] py-2.5 pl-5 pr-2.5', FLOATING)}
      >
        <span className="h-2 w-2 shrink-0 rounded-full bg-warn" aria-hidden />
        <p className="min-w-0 flex-1 text-[13px]">
          {problems ? (
            <span className="inline-flex items-center gap-1.5 text-bad">
              <TriangleAlert size={14} /> {problems === 1 ? 'One value needs attention' : `${problems} values need attention`}
            </span>
          ) : settings.needsSave ? (
            (label ?? 'You have unsaved changes')
          ) : (
            'Your last change couldn’t be saved'
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
          {settings.saving ? 'Saving…' : settings.needsSave ? 'Save' : 'Try again'}
        </Button>
      </div>
    </div>
  );
}
