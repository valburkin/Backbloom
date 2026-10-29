import { Radio } from 'lucide-react';
import { Section } from '../../components/form';
import { SchemaForm } from '../../components/SchemaForm';
import type { PageSettings } from '../../app/config';

type Obj = Record<string, unknown>;

/** Settings → Integrations: MQTT for Home Assistant, Node-RED and the like (topic, reconnects… under More options). */
export function IntegrationsTab({ ps }: { ps: PageSettings }) {
  const schema = ps.schemaOf('mqtt');
  const mqtt = ps.value<Obj>('mqtt');
  const issues = ps.issues.filter((i) => i.path.startsWith('mqtt.')).map((i) => ({ ...i, path: i.path.slice(5) }));
  if (!schema) return <p className="text-[13px] text-ink-faint">This version of Backbloom has no MQTT support.</p>;
  return (
    <Section title="MQTT" icon={<Radio size={18} />} description="Control your lights from Home Assistant, Node-RED and similar systems over MQTT.">
      <SchemaForm
        schema={schema}
        value={mqtt}
        issues={issues}
        onChange={(v) => ps.set('mqtt', v)}
        omit={mqtt.enable === true ? [] : Object.keys(schema.properties ?? {}).filter((k) => k !== 'enable')}
        overrides={{
          enable: { label: 'Connect to an MQTT broker', hint: 'Backbloom publishes its state and accepts commands.' },
          port: { label: 'Broker port' },
        }}
      />
    </Section>
  );
}
