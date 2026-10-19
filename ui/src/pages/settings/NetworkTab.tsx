import { Globe, LockKeyhole } from 'lucide-react';
import { Advanced, Callout, Input, NumberField, Section } from '../../components/form';
import type { PageSettings } from '../../app/config';
import { useConnection } from '../../app/connection';
import { deepEqual } from '../../settings/drafts';
import { propertyAt, rangeOf } from '../../schema/schema';

type Obj = Record<string, unknown>;

export function NetworkTab({ ps }: { ps: PageSettings }) {
  const { backend } = useConnection();
  const web = ps.value<Obj>('webConfig');
  const set = (k: string) => (v: unknown) => ps.setPath('webConfig', [k], v);
  const err = (p: string) => ps.issues.find((i) => i.path === p)?.message;
  const port = rangeOf(propertyAt(ps.schemaOf('webConfig'), 'port'), { min: 80, max: 65535 });
  const before = ps.baseline.webConfig as Obj | undefined;
  const portChanged = !!before && (!deepEqual(before.port, web.port) || !deepEqual(before.sslPort, web.sslPort));
  const host = backend.mode === 'live' ? new URL(backend.url.replace(/^ws/, 'http')).hostname : 'localhost';

  return (
    <>
      <Section title="Web interface" icon={<Globe size={18} />} description="The ports this page and apps use to reach Overglow.">
        <div className="grid gap-4 sm:grid-cols-2">
          <NumberField integer label="Port (http)" min={port.min} max={port.max} value={Number(web.port)} error={err('webConfig.port')} onChange={set('port')} />
          <NumberField integer label="Secure port (https)" min={port.min} max={port.max} value={Number(web.sslPort)} error={err('webConfig.sslPort')} onChange={set('sslPort')} />
        </div>
        {portChanged && (
          <Callout tone="warn">
            After saving, this page moves to <span className="font-mono">http://{host}:{String(web.port)}</span>. Bookmarks and apps that use the old port need updating.
          </Callout>
        )}
      </Section>
      <Section title="Your own certificate" icon={<LockKeyhole size={18} />} description="Overglow uses a self-signed certificate for https unless you provide one.">
        <Advanced label="Certificate files">
          <Input label="Certificate file (PEM)" placeholder="C:\certs\overglow.crt" value={String(web.crtPath ?? '')} onChange={set('crtPath')} />
          <Input label="Private key file (PEM)" placeholder="C:\certs\overglow.key" value={String(web.keyPath ?? '')} onChange={set('keyPath')} />
          <Input label="Key password" type="password" autoComplete="new-password" hint="Only if the key is protected with a password." value={String(web.keyPassPhrase ?? '')} onChange={set('keyPassPhrase')} />
        </Advanced>
      </Section>
    </>
  );
}
