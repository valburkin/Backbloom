import { useMemo, useState } from 'react';
import { LayoutTemplate as TemplateIcon, Sparkles } from 'lucide-react';
import {
  splitByAspect,
  TEMPLATES,
  type Edge,
  type LayoutTemplate,
  type Segment,
  type TemplateParam,
  type TemplateValue,
} from '../../layout';
import { Button, cx, Dialog, EmptyState, SliderField, Stepper } from '../../components/ui';
import { describeSegment } from '../../editor/segments';

const EDGES: Edge[] = ['top', 'right', 'bottom', 'left'];

function defaults(t: LayoutTemplate): Record<string, TemplateValue> {
  return Object.fromEntries(t.params.map((p) => [p.key, p.default]));
}

function ParamField({ param, value, onChange }: { param: TemplateParam; value: TemplateValue; onChange: (v: TemplateValue) => void }) {
  const id = `tpl-${param.key}`;
  return (
    <div className="space-y-1">
      {param.type === 'number' &&
        ((param.step ?? 1) >= 1 ? (
          <Stepper label={param.label} value={Number(value)} min={param.min ?? 0} max={param.max ?? 2000} onChange={onChange} />
        ) : (
          <SliderField
            label={param.label}
            value={Number(value)}
            min={param.min ?? 0}
            max={param.max ?? 1}
            step={param.step ?? 0.01}
            onChange={onChange}
          />
        ))}
      {param.type === 'select' && (
        <div className="flex items-center justify-between gap-3">
          <label htmlFor={id} className="text-[13px] font-medium">
            {param.label}
          </label>
          <select id={id} className="h-8 rounded-lg border border-line bg-bg px-2 text-sm" value={String(value)} onChange={(e) => onChange(e.target.value)}>
            {(param.options ?? []).map((o) => (
              <option key={o.value} value={o.value}>
                {o.label}
              </option>
            ))}
          </select>
        </div>
      )}
      {param.type === 'boolean' && (
        <label htmlFor={id} className="flex cursor-pointer items-center justify-between gap-3">
          <span className="text-[13px] font-medium">{param.label}</span>
          <input id={id} type="checkbox" className="h-4 w-4 accent-[var(--color-accent)]" checked={Boolean(value)} onChange={(e) => onChange(e.target.checked)} />
        </label>
      )}
      {param.help && <p className="text-xs text-ink-faint">{param.help}</p>}
    </div>
  );
}

/**
 * "I have N LEDs": spread a total over the template's per-edge count params (keys top/right/bottom/left)
 * in proportion to the screen shape; single-run templates (key "count") just take the total.
 * Returns null when the template has no count params to fill.
 */
export function fillTotal(
  t: LayoutTemplate,
  total: number,
  aspect: { w: number; h: number },
  values: Record<string, TemplateValue>,
): Record<string, TemplateValue> | null {
  const keys = new Set(t.params.filter((p) => p.type === 'number').map((p) => p.key));
  const sides = EDGES.filter((e) => keys.has(e));
  if (sides.length) {
    const split = splitByAspect(total, aspect.w, aspect.h, sides);
    return { ...values, ...Object.fromEntries(sides.map((e) => [e, split[e]])) };
  }
  const countParam = t.params.find((p) => p.key === 'count');
  if (countParam) {
    const perBar = /per bar/i.test(countParam.label) ? Math.floor(total / 2) : total;
    return { ...values, count: perBar };
  }
  return null;
}

export function TemplateDialog({
  open,
  onClose,
  stripName,
  outputLength,
  aspect,
  onApply,
}: {
  open: boolean;
  onClose: () => void;
  stripName: string | null;
  outputLength: number | undefined;
  aspect: { w: number; h: number };
  onApply: (segments: Segment[]) => void;
}) {
  const [templateId, setTemplateId] = useState<string | null>(null);
  const template = TEMPLATES.find((t) => t.id === templateId) ?? null;
  const [values, setValues] = useState<Record<string, TemplateValue>>({});
  const [total, setTotal] = useState<number>(outputLength ?? 100);
  const [splitNote, setSplitNote] = useState<string | null>(null);

  const pick = (t: LayoutTemplate) => {
    setTemplateId(t.id);
    if (outputLength !== undefined) setTotal(outputLength);
    setValues(defaults(t));
    setSplitNote(null);
  };

  const preview = useMemo(() => {
    if (!template) return null;
    try {
      const segments = template.build(values);
      return { segments, error: null as string | null, count: segments.reduce((n, s) => n + s.count, 0) };
    } catch (e) {
      return { segments: [] as Segment[], error: e instanceof Error ? e.message : String(e), count: 0 };
    }
  }, [template, values]);

  const close = () => {
    setTemplateId(null);
    onClose();
  };

  return (
    <Dialog
      open={open}
      onClose={close}
      wide
      title={template ? template.name : 'Start from a template'}
      footer={
        template ? (
          <>
            <Button variant="ghost" onClick={() => setTemplateId(null)}>
              Back
            </Button>
            <Button
              variant="primary"
              disabled={!stripName || !preview || !!preview.error || !preview.segments.length}
              onClick={() => {
                if (preview) onApply(preview.segments);
                close();
              }}
            >
              {stripName ? `Use for “${stripName}”` : 'Select a strip first'}
            </Button>
          </>
        ) : undefined
      }
    >
      {!template ? (
        TEMPLATES.length === 0 ? (
          <EmptyState icon={<TemplateIcon size={28} />} title="No templates available yet">
            Templates for common setups are on their way. Meanwhile, build the strip from parts on the right.
          </EmptyState>
        ) : (
          <div className="grid grid-cols-1 gap-3 sm:grid-cols-2">
            {TEMPLATES.map((t) => (
              <button
                key={t.id}
                type="button"
                onClick={() => pick(t)}
                className="rounded-xl border border-line bg-surface-2 p-4 text-left transition-colors hover:border-accent/60"
              >
                <p className="text-sm font-semibold">{t.name}</p>
                <p className="mt-1 text-[13px] text-ink-muted">{t.description}</p>
              </button>
            ))}
          </div>
        )
      ) : (
        <div className="grid grid-cols-1 gap-6 md:grid-cols-2">
          <div className="space-y-4">
            <p className="text-[13px] text-ink-muted">{template.description}</p>
            {fillTotal(template, total, aspect, values) && (
              <div className="space-y-2 rounded-xl border border-accent/30 bg-accent/5 p-3">
                <p className="flex items-center gap-1.5 text-[13px] font-medium text-accent-strong">
                  <Sparkles size={14} /> I have this many LEDs in total
                </p>
                <div className="flex items-center gap-2">
                  <Stepper label="Total LEDs" value={total} min={1} max={5000} onChange={setTotal} className="flex-1" />
                  <Button
                    size="sm"
                    onClick={() => {
                      try {
                        const next = fillTotal(template, total, aspect, values);
                        if (next) {
                          setValues(next);
                          setSplitNote(null);
                        }
                      } catch (e) {
                        setSplitNote(e instanceof Error ? e.message : String(e));
                      }
                    }}
                  >
                    Split for me
                  </Button>
                </div>
                {splitNote && <p className="text-xs text-warn">{splitNote}</p>}
              </div>
            )}
            {template.params.map((p) => (
              <ParamField key={p.key} param={p} value={values[p.key] ?? p.default} onChange={(v) => setValues((s) => ({ ...s, [p.key]: v }))} />
            ))}
          </div>
          <div className="rounded-xl border border-line bg-surface-2 p-4">
            <p className="text-xs font-semibold uppercase tracking-wider text-ink-faint">Preview</p>
            {preview?.error ? (
              <p className="mt-2 text-[13px] text-bad">{preview.error}</p>
            ) : (
              <>
                <p className={cx('mt-2 text-sm font-medium', outputLength !== undefined && preview?.count !== outputLength && 'text-warn')}>
                  {preview?.count ?? 0} LEDs
                  {outputLength !== undefined && preview?.count !== outputLength && ` — the output has ${outputLength}`}
                </p>
                <ol className="mt-3 space-y-1 text-[13px] text-ink-muted">
                  {preview?.segments.map((s, i) => (
                    <li key={i}>
                      {i + 1}. {describeSegment(s)}
                    </li>
                  ))}
                </ol>
              </>
            )}
          </div>
        </div>
      )}
    </Dialog>
  );
}
