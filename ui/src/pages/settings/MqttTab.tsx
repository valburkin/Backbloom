import { Radio } from 'lucide-react';
import { Section } from '../../components/form';
import { SchemaForm } from '../../components/SchemaForm';
import type { PageSettings } from '../../app/config';

type Obj = Record<string, unknown>;

export function MqttTab({ ps }: { ps: PageSettings }) {
  const schema = ps.schemaOf('mqtt');
  const mqtt = ps.value<Obj>('mqtt');
  const issues = ps.issues.filter((i) => i.path.startsWith('mqtt.')).map((i) => ({ ...i, path: i.path.slice(5) }));
  if (!schema) return <p className="text-[13px] text-ink-faint">This version of the service has no MQTT support.</p>;
  return (
    <Section
      title="MQTT"
      icon={<Radio size={18} />}
      description="Connect Overglow to a home automation system (Home Assistant, Node-RED…) through an MQTT broker. It publishes its state and accepts commands."
    >
      <SchemaForm
        schema={schema}
        value={mqtt}
        issues={issues}
        onChange={(v) => ps.set('mqtt', v)}
        omit={mqtt.enable === true ? [] : Object.keys(schema.properties ?? {}).filter((k) => k !== 'enable')}
        overrides={{
          enable: { label: 'Connect to an MQTT broker' },
          port: { label: 'Broker port' },
          maxRetry: { label: 'Reconnect attempts' },
        }}
      />
    </Section>
  );
}
