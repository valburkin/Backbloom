import type { ReactNode } from 'react';
import { Plus, Trash2 } from 'lucide-react';
import type { JsonSchema } from '../api/types';
import { isRgb, type Rgb } from '../color/color';
import { useExpertTools } from '../app/expert';
import { dependenciesMet, differsFromDefault, enumOptions, fieldHelp, fieldLabel, fieldsOf, fieldUnit, isObject, withDefaults, type FieldDef, type Issue } from '../schema/schema';
import { ColorPicker } from './ColorPicker';
import { Advanced, Input, NumberField, Select, Toggle } from './form';
import { Button, IconButton } from './ui';

type Obj = Record<string, unknown>;

export interface FieldOverride {
  label?: string;
  hint?: ReactNode;
  /** Shown next to text inputs (e.g. a "Find devices" button). */
  trailing?: ReactNode;
  /** Replace the control entirely. */
  render?: (value: unknown, onChange: (v: unknown) => void, error?: string) => ReactNode;
}

export interface SchemaFormProps {
  schema: JsonSchema;
  value: Obj;
  onChange: (next: Obj) => void;
  /** Validation problems; paths relative to `value`. */
  issues?: Issue[];
  /** Keys rendered elsewhere on the page. */
  omit?: readonly string[];
  overrides?: Record<string, FieldOverride>;
  disabled?: boolean;
  /** Put advanced fields in a collapsed "More options" group and hide expert ones unless expert tools are on (default true). */
  collapseAdvanced?: boolean;
}

function issuesBelow(issues: Issue[] | undefined, key: string): Issue[] {
  return (issues ?? []).filter((i) => i.path.startsWith(`${key}.`)).map((i) => ({ ...i, path: i.path.slice(key.length + 1) }));
}

/**
 * Renders an object schema from the backend as a form: switches, selects, number fields with units, text,
 * color pickers, lists of values and lists of objects. Fields whose `options.dependencies` aren't met are
 * hidden. Tiers (docs/ui-guidelines.md): advanced fields go into a collapsed "More options" group, expert fields are
 * only shown with expert tools on (or while they have a problem or a non-default value, so nothing is hidden that matters).
 */
export function SchemaForm({ schema, value, onChange, issues, omit = [], overrides = {}, disabled, collapseAdvanced = true }: SchemaFormProps) {
  const expertTools = useExpertTools();
  const errorAt = (key: string) => issues?.some((i) => i.path === key || i.path.startsWith(`${key}.`)) ?? false;
  const fields = fieldsOf(schema).filter(
    (f) =>
      !omit.includes(f.key) &&
      dependenciesMet(f.schema, value) &&
      (!collapseAdvanced || f.tier !== 'expert' || expertTools || errorAt(f.key) || differsFromDefault(f.schema, value[f.key])),
  );
  const set = (key: string, v: unknown) => onChange({ ...value, [key]: v });
  const render = (f: FieldDef) => (
    <SchemaField
      key={f.key}
      field={f}
      value={value[f.key]}
      onChange={(v) => set(f.key, v)}
      error={issues?.find((i) => i.path === f.key)?.message}
      nested={issuesBelow(issues, f.key)}
      override={overrides[f.key]}
      disabled={disabled}
    />
  );
  const basic = collapseAdvanced ? fields.filter((f) => f.tier === 'basic') : fields;
  const advanced = collapseAdvanced ? fields.filter((f) => f.tier !== 'basic') : [];
  const changed = advanced.filter((f) => differsFromDefault(f.schema, value[f.key]));
  if (!fields.length) return <p className="text-[13px] text-ink-faint">Nothing to set up here.</p>;
  return (
    <div className="space-y-5">
      {basic.map(render)}
      {advanced.length > 0 && (
        <Advanced
          changed={changed.length}
          invalid={advanced.some((f) => errorAt(f.key))}
          onReset={disabled ? undefined : () => onChange({ ...value, ...Object.fromEntries(changed.map((f) => [f.key, withDefaults(f.schema, undefined)])) })}
        >
          {advanced.map(render)}
        </Advanced>
      )}
    </div>
  );
}

function SchemaField({
  field,
  value,
  onChange,
  error,
  nested,
  override,
  disabled,
}: {
  field: FieldDef;
  value: unknown;
  onChange: (v: unknown) => void;
  error?: string;
  nested: Issue[];
  override?: FieldOverride;
  disabled?: boolean;
}) {
  const s = field.schema;
  const label = override?.label ?? fieldLabel(field.key, s);
  const hint = override?.hint ?? fieldHelp(s);
  if (override?.render) return <>{override.render(value, onChange, error)}</>;

  if (s.enum) {
    return (
      <Select
        label={label}
        hint={hint}
        error={error}
        disabled={disabled}
        value={value as string | number | undefined}
        options={enumOptions(s)}
        onChange={onChange}
      />
    );
  }
  switch (s.type) {
    case 'boolean':
      return <Toggle label={label} hint={hint} checked={value === true} disabled={disabled} onChange={onChange} />;
    case 'integer':
    case 'number':
      return (
        <NumberField
          label={label}
          hint={hint}
          error={error}
          required={field.required}
          disabled={disabled}
          value={typeof value === 'number' ? value : undefined}
          min={s.minimum}
          max={s.maximum}
          step={s.step}
          unit={fieldUnit(s)}
          integer={s.type === 'integer'}
          onChange={onChange}
        />
      );
    case 'string':
      return (
        <Input
          label={label}
          hint={hint}
          error={error}
          required={field.required}
          disabled={disabled}
          type={s.format === 'password' ? 'password' : 'text'}
          autoComplete={s.format === 'password' ? 'new-password' : 'off'}
          value={typeof value === 'string' ? value : ''}
          trailing={override?.trailing}
          onChange={onChange}
        />
      );
    case 'array':
      if (s.format === 'colorpicker') {
        return (
          <div className="space-y-1.5">
            <p className="text-[13px] font-medium">{label}</p>
            <ColorPicker label={label} compact value={isRgb(value) ? value : ([255, 255, 255] as Rgb)} onChange={(rgb) => onChange([...rgb])} />
          </div>
        );
      }
      return <ListField label={label} hint={hint} error={error} schema={s} value={Array.isArray(value) ? value : []} onChange={onChange} issues={nested} disabled={disabled} />;
    case 'object':
      if (!s.properties || !Object.keys(s.properties).length) return null;
      return (
        <fieldset className="space-y-4 rounded-xl border border-line p-4">
          <legend className="px-1 text-[13px] font-medium">{label}</legend>
          <SchemaForm schema={s} value={isObject(value) ? value : {}} onChange={onChange} issues={nested} disabled={disabled} />
        </fieldset>
      );
    default:
      return null;
  }
}

function ListField({
  label,
  hint,
  error,
  schema,
  value,
  onChange,
  issues,
  disabled,
}: {
  label: string;
  hint?: ReactNode;
  error?: string;
  schema: JsonSchema;
  value: unknown[];
  onChange: (v: unknown[]) => void;
  issues: Issue[];
  disabled?: boolean;
}) {
  const item = schema.items ?? { type: 'string' };
  const itemLabel = fieldLabel('item', item) || 'Item';
  const isObj = item.type === 'object' && !!item.properties;
  const setAt = (i: number, v: unknown) => onChange(value.map((x, j) => (j === i ? v : x)));
  return (
    <div className="space-y-2">
      <div>
        <p className="text-[13px] font-medium">{label}</p>
        {(hint || error) && <p className={error ? 'text-xs text-bad' : 'text-xs text-ink-faint'}>{error ?? hint}</p>}
      </div>
      {value.length === 0 && <p className="text-[13px] text-ink-faint">None yet.</p>}
      <ul className="space-y-2">
        {value.map((v, i) => (
          <li key={i} className={isObj ? 'rounded-xl border border-line p-3' : 'flex items-end gap-2'}>
            {isObj ? (
              <>
                <div className="mb-2 flex items-center justify-between">
                  <span className="text-xs font-medium text-ink-muted">
                    {itemLabel} {i + 1}
                  </span>
                  <IconButton label={`Remove ${itemLabel.toLowerCase()} ${i + 1}`} disabled={disabled} onClick={() => onChange(value.filter((_, j) => j !== i))}>
                    <Trash2 size={15} />
                  </IconButton>
                </div>
                <SchemaForm
                  schema={item}
                  value={isObject(v) ? v : {}}
                  onChange={(nv) => setAt(i, nv)}
                  issues={issuesBelow(issues, String(i))}
                  disabled={disabled}
                  collapseAdvanced={false}
                />
              </>
            ) : (
              <>
                <Input
                  className="flex-1"
                  label={`${itemLabel} ${i + 1}`}
                  value={typeof v === 'string' ? v : String(v ?? '')}
                  error={issues.find((x) => x.path === String(i))?.message}
                  disabled={disabled}
                  onChange={(nv) => setAt(i, nv)}
                />
                <IconButton label={`Remove ${itemLabel.toLowerCase()} ${i + 1}`} className="mb-1" disabled={disabled} onClick={() => onChange(value.filter((_, j) => j !== i))}>
                  <Trash2 size={15} />
                </IconButton>
              </>
            )}
          </li>
        ))}
      </ul>
      <Button
        size="sm"
        variant="ghost"
        icon={<Plus size={15} />}
        disabled={disabled || (schema.maxItems !== undefined && value.length >= schema.maxItems)}
        onClick={() => onChange([...value, isObj ? withDefaults(item, {}) : item.type === 'string' ? '' : (item.default ?? '')])}
      >
        Add {itemLabel.toLowerCase()}
      </Button>
    </div>
  );
}
