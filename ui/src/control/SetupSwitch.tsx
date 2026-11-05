import { useConnection } from '../app/connection';
import { useEditor } from '../app/editor';
import { useAction } from '../app/useAction';
import { Segmented } from '../components/ui';
import { EVERYDAY, presetSummary, switchToEveryday } from './presets';

/**
 * Everyday and the presets as one switch, always one in use: a preset is picked with `profile select`, Everyday with
 * switchToEveryday (a preset its program turned on stays unused until the program quits). Nothing without presets.
 */
export function SetupSwitch({ size = 'md' }: { size?: 'sm' | 'md' }) {
  const { serverInfo, api } = useConnection();
  const { state } = useEditor();
  const { run, busy } = useAction();
  const presets = state.spec.profiles ?? [];
  const active = serverInfo?.profile ?? null;
  if (!presets.length) return null;
  return (
    <Segmented
      label="Setup in use"
      size={size}
      value={active?.id ?? ''}
      options={[
        { value: '', label: EVERYDAY, title: 'Your strips as Layout has them, with your Look' },
        ...presets.map((p) => ({ value: p.id, label: p.name || 'Untitled', title: presetSummary(state.spec, p) })),
      ]}
      onChange={(id) => {
        if (busy === 'setup' || id === (active?.id ?? '')) return;
        const p = presets.find((x) => x.id === id);
        if (p) void run('setup', `Couldn’t switch to ${p.name}`, () => api.setProfile(p.id));
        else void run('setup', `Couldn’t switch to ${EVERYDAY}`, () => switchToEveryday(api));
      }}
    />
  );
}
