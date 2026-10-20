// Helpers for the backend's settings schemas (JSON schema draft-03 style plus json-editor `options`).
// Only the features the backend's schemas actually use are handled: types, enums (+ enum_titles), minimum /
// maximum / step, min/maxLength, min/maxItems, pattern, defaults, `required` (boolean or list),
// `options.hidden`, `options.dependencies` and `access` (expert/advanced → shown under "Advanced").

import type { JsonSchema } from '../api/types';
import { SCHEMA_TEXT } from './text';

export interface FieldDef {
  key: string;
  schema: JsonSchema;
  required: boolean;
  /** Marked expert/advanced in the schema; shown collapsed. */
  advanced: boolean;
}

export interface Issue {
  /** Dotted path below the validated value, e.g. "host" or "lamps.0.name". */
  path: string;
  message: string;
}

type Obj = Record<string, unknown>;

export function isObject(v: unknown): v is Obj {
  return !!v && typeof v === 'object' && !Array.isArray(v);
}

/** Visible properties of an object schema, in the schema's property order. */
export function fieldsOf(schema: JsonSchema | undefined): FieldDef[] {
  const props = schema?.properties ?? {};
  const requiredList = Array.isArray(schema?.required) ? schema.required : [];
  return Object.entries(props)
    .filter(([, s]) => !s.options?.hidden)
    .map(([key, s]) => ({
      key,
      schema: s,
      required: s.required === true || requiredList.includes(key),
      advanced: s.access === 'expert' || s.access === 'advanced',
    }))
    .sort((a, b) => (a.schema.propertyOrder ?? 999) - (b.schema.propertyOrder ?? 999) || a.key.localeCompare(b.key));
}

/** "brightnessMaxLevel" → "Brightness max level". */
export function humanize(key: string): string {
  const words = key
    .replace(/_/g, ' ')
    .replace(/([a-z0-9])([A-Z])/g, '$1 $2')
    .trim()
    .toLowerCase();
  return words.charAt(0).toUpperCase() + words.slice(1);
}

function text(key: string | undefined): readonly [string, string?] | undefined {
  return key ? SCHEMA_TEXT[key] : undefined;
}

/** Display label of a field: known title text, a plain-language title, or the property name made readable. */
export function fieldLabel(key: string, schema: JsonSchema): string {
  const t = text(schema.title);
  if (t) return t[0];
  if (schema.title && !/^[a-z0-9]+(_[a-z0-9]+)+$/i.test(schema.title)) return schema.title;
  return humanize(key);
}

export function fieldHelp(schema: JsonSchema): string | undefined {
  return text(schema.title)?.[1];
}

/** Unit shown after a number ("ms", "%", "Hz"...). */
export function fieldUnit(schema: JsonSchema): string | undefined {
  if (!schema.append) return undefined;
  return text(schema.append)?.[0] ?? (schema.append.includes('_') ? undefined : schema.append);
}

export function enumOptions(schema: JsonSchema): Array<{ value: string | number; label: string }> {
  const titles = schema.options?.enum_titles ?? [];
  return (schema.enum ?? []).map((value, i) => {
    const t = titles[i];
    const label = (t && text(t)?.[0]) ?? (t && !t.includes('_') ? t : String(value));
    return { value, label };
  });
}

function matches(expected: unknown, actual: unknown): boolean {
  if (Array.isArray(expected)) return expected.some((e) => matches(e, actual));
  if (expected === true && typeof actual !== 'boolean') return !!actual;
  return expected === actual;
}

/** Whether a field's `options.dependencies` are satisfied by its sibling values. */
export function dependenciesMet(schema: JsonSchema, siblings: unknown): boolean {
  const deps = schema.options?.dependencies;
  if (!deps) return true;
  const values = isObject(siblings) ? siblings : {};
  return Object.entries(deps).every(([key, expected]) => matches(expected, values[key]));
}

function clone<T>(v: T): T {
  return v === undefined ? v : (JSON.parse(JSON.stringify(v)) as T);
}

/** Value with every missing property filled from the schema defaults (objects are filled recursively). */
export function withDefaults(schema: JsonSchema | undefined, value: unknown): unknown {
  if (!schema) return value;
  if (schema.type === 'object' || schema.properties) {
    const src = isObject(value) ? value : isObject(schema.default) ? (schema.default as Obj) : {};
    const out: Obj = { ...src };
    for (const [key, s] of Object.entries(schema.properties ?? {})) {
      if (out[key] === undefined) {
        if (s.default !== undefined) out[key] = clone(s.default);
        else if (s.type === 'object' && s.properties) out[key] = withDefaults(s, undefined);
      } else if (s.type === 'object' && s.properties) {
        out[key] = withDefaults(s, out[key]);
      }
    }
    return out;
  }
  return value === undefined ? clone(schema.default) : value;
}

function typeOk(schema: JsonSchema, v: unknown): boolean {
  switch (schema.type) {
    case 'integer':
      return typeof v === 'number' && Number.isInteger(v);
    case 'number':
      return typeof v === 'number' && Number.isFinite(v);
    case 'string':
      return typeof v === 'string';
    case 'boolean':
      return typeof v === 'boolean';
    case 'array':
      return Array.isArray(v);
    case 'object':
      return isObject(v);
    default:
      return true;
  }
}

/**
 * Checks a value against the schema the way the backend would, so problems are shown while editing, before the
 * backend would reject the save. Fields hidden by unmet dependencies are not checked.
 */
export function validate(schema: JsonSchema, value: unknown, path = '', required = false): Issue[] {
  const at = (message: string): Issue[] => [{ path, message }];
  if (value === undefined || value === null) return required ? at('This is required.') : [];
  if (!typeOk(schema, value)) {
    if (schema.type === 'integer' && typeof value === 'number') return at('Use a whole number.');
    return at(schema.type === 'number' || schema.type === 'integer' ? 'Enter a number.' : `Expected a ${schema.type}.`);
  }
  if (schema.enum && !schema.enum.includes(value as string | number)) return at('Pick one of the listed options.');
  if (typeof value === 'number') {
    if (schema.minimum !== undefined && value < schema.minimum) return at(`The minimum is ${schema.minimum}.`);
    if (schema.maximum !== undefined && value > schema.maximum) return at(`The maximum is ${schema.maximum}.`);
  }
  if (typeof value === 'string') {
    if (required && value.trim() === '' && !schema.enum && schema.default === undefined) return at('This is required.');
    if (schema.minLength !== undefined && value.length < schema.minLength) return at(`Use at least ${schema.minLength} characters.`);
    if (schema.maxLength !== undefined && value.length > schema.maxLength) return at(`Use at most ${schema.maxLength} characters.`);
    if (schema.pattern && value !== '' && !new RegExp(schema.pattern).test(value)) return at('This doesn’t look right.');
  }
  if (Array.isArray(value)) {
    if (schema.minItems !== undefined && value.length < schema.minItems) return at(`Add at least ${schema.minItems}.`);
    if (schema.maxItems !== undefined && value.length > schema.maxItems) return at(`Use at most ${schema.maxItems}.`);
    if (schema.items) return value.flatMap((v, i) => validate(schema.items!, v, path ? `${path}.${i}` : String(i)));
  }
  if (isObject(value) && schema.properties) {
    const requiredList = Array.isArray(schema.required) ? schema.required : [];
    return Object.entries(schema.properties).flatMap(([key, s]) => {
      if (s.options?.hidden || !dependenciesMet(s, value)) return [];
      return validate(s, value[key], path ? `${path}.${key}` : key, s.required === true || requiredList.includes(key));
    });
  }
  return [];
}

/** Numeric range of a property, for bespoke controls that still honor the backend's limits. */
export function rangeOf(schema: JsonSchema | undefined, fallback: { min: number; max: number; step?: number }) {
  return {
    min: schema?.minimum ?? fallback.min,
    max: schema?.maximum ?? fallback.max,
    step: schema?.step ?? fallback.step ?? 1,
    default: schema?.default as number | undefined,
  };
}

/** Schema of a nested property: propertyAt(schema, 'smoothing', 'time_ms'). */
export function propertyAt(schema: JsonSchema | undefined, ...path: string[]): JsonSchema | undefined {
  let s = schema;
  for (const key of path) {
    if (!s) return undefined;
    s = key === '[]' ? s.items : s.properties?.[key];
  }
  return s;
}
