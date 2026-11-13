import { useEffect, useState, type ReactNode } from 'react';
import { Bug, Copy, Info, LifeBuoy, MessageCircleQuestion, Scale } from 'lucide-react';
import { buttonClass, IconButton } from '../../components/ui';
import { Section } from '../../components/form';
import { Logo } from '../../components/Logo';
import { useConnection } from '../../app/connection';
import { useToast } from '../../app/toast';
import { parseLicenses, UI_LIBRARIES, type LicenseEntry } from '../../settings/licenses';
import { bugReportUrl, DISCUSSIONS_URL, KNOWN_ISSUES_URL } from '../../settings/help';

function Row({ label, children }: { label: string; children: ReactNode }) {
  return (
    <div className="grid grid-cols-[9rem_1fr] gap-3 border-b border-line py-2 text-[13px] last:border-0">
      <dt className="text-ink-muted">{label}</dt>
      <dd className="min-w-0 break-words">{children}</dd>
    </div>
  );
}

function LicenseText({ text }: { text: string }) {
  return <pre className="scroll-thin mt-2 max-h-64 overflow-auto whitespace-pre-wrap rounded-lg bg-bg p-3 font-mono text-[11px] leading-relaxed text-ink-muted">{text}</pre>;
}

export function AboutTab() {
  const { sysInfo, serverInfo } = useConnection();
  const toast = useToast();
  const [licenses, setLicenses] = useState<{ own: string; thirdParty: LicenseEntry[] } | null>(null);
  useEffect(() => {
    let alive = true;
    void Promise.all([import('../../../../LICENSE?raw'), import('../../../../3RD_PARTY_LICENSES?raw')]).then(([own, third]) => {
      if (alive) setLicenses({ own: own.default, thirdParty: parseLicenses(third.default) });
    });
    return () => {
      alive = false;
    };
  }, []);
  const o = sysInfo?.backbloom;
  const s = sysInfo?.system;
  const report = [
    `Backbloom ${o?.version ?? '?'} ${o?.build ?? ''}`.trim(),
    o?.time ? `Built ${o.time}` : '',
    s ? `${s.prettyName ?? s.productType ?? ''} (${s.kernelType ?? ''} ${s.kernelVersion ?? ''}, ${s.architecture ?? ''})` : '',
    s?.runtimeVersion ?? '',
    serverInfo?.systemGrabbers?.device ? `Screen capture: ${serverInfo.systemGrabbers.device}` : '',
    `LEDs: ${serverInfo?.leds?.length ?? 0}`,
  ]
    .filter(Boolean)
    .join('\n');

  return (
    <>
      <Section
        title="Backbloom"
        icon={<Info size={18} />}
        actions={
          <IconButton label="Copy system report" onClick={() => void navigator.clipboard?.writeText(report).then(() => toast('ok', 'System report copied'))}>
            <Copy size={16} />
          </IconButton>
        }
      >
        <div className="flex items-center gap-4">
          <Logo size={48} />
          <div>
            <p className="text-lg font-semibold">Backbloom</p>
            <p className="text-[13px] text-ink-muted">Ambient lighting that follows your screen.</p>
          </div>
        </div>
        <dl>
          <Row label="Version">{o?.version ?? '—'}</Row>
          <Row label="Build">{o?.build?.trim() || '—'}</Row>
          <Row label="Built on">{o?.time ?? '—'}</Row>
          <Row label="PC">{s?.hostName ?? '—'}</Row>
          <Row label="System">{[s?.prettyName, s?.architecture].filter(Boolean).join(' · ') || '—'}</Row>
          <Row label="Kernel">{[s?.kernelType, s?.kernelVersion].filter(Boolean).join(' ') || '—'}</Row>
          <Row label="Runtime">{s?.runtimeVersion ?? '—'}</Row>
          <Row label="Screen capture">{serverInfo?.systemGrabbers?.device ?? '—'}</Row>
        </dl>
      </Section>

      <Section title="Get help" icon={<LifeBuoy size={18} />}>
        <p className="text-[13px] text-ink-muted">
          Something not working? Check the{' '}
          <a className="text-accent-strong hover:underline" href={KNOWN_ISSUES_URL} target="_blank" rel="noreferrer">
            known issues
          </a>{' '}
          first: if yours is there, give it a thumbs-up or add your details. Otherwise report it on GitHub; the form opens with this
          PC's system report filled in.
        </p>
        <div className="flex flex-wrap gap-2">
          <a className={buttonClass('primary')} href={bugReportUrl(o?.version, report)} target="_blank" rel="noreferrer">
            <Bug size={16} aria-hidden="true" />
            Report a problem
          </a>
          <a className={buttonClass('secondary')} href={DISCUSSIONS_URL} target="_blank" rel="noreferrer">
            <MessageCircleQuestion size={16} aria-hidden="true" />
            Ask a question
          </a>
        </div>
      </Section>

      <Section title="Licenses" icon={<Scale size={18} />}>
        <div>
          <p className="text-[13px] font-medium">Backbloom {o?.version ?? ''} · MIT License</p>
          <p className="text-[13px] text-ink-muted">
            Open source:{' '}
            <a className="text-accent-strong hover:underline" href="https://github.com/valburkin/Backbloom" target="_blank" rel="noreferrer">
              github.com/valburkin/Backbloom
            </a>
          </p>
        </div>
        <details>
          <summary className="cursor-pointer text-[13px] font-medium">License text (based on HyperHDR by awawa-dev)</summary>
          {licenses ? <LicenseText text={licenses.own} /> : <p className="mt-2 text-xs text-ink-faint">Loading…</p>}
        </details>
        <div>
          <p className="mb-2 text-[13px] font-medium">Third-party software</p>
          <ul className="divide-y divide-line rounded-xl border border-line">
            {UI_LIBRARIES.map((l) => (
              <li key={l.name} className="flex items-center justify-between px-3 py-2 text-[13px]">
                <span>{l.name}</span>
                <span className="text-xs text-ink-faint">{l.license} · web interface</span>
              </li>
            ))}
            {licenses?.thirdParty.map((l) => (
              <li key={l.name} className="px-3 py-2 text-[13px]">
                <details>
                  <summary className="cursor-pointer">{l.name}</summary>
                  <LicenseText text={l.text} />
                </details>
              </li>
            ))}
          </ul>
        </div>
      </Section>
    </>
  );
}
