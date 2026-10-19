export interface LicenseEntry {
  name: string;
  text: string;
}

/**
 * Splits the repository's 3RD_PARTY_LICENSES file into entries. Each entry starts with a heading framed by
 * lines of "=" characters.
 */
export function parseLicenses(text: string): LicenseEntry[] {
  const lines = text.replace(/\r\n?/g, '\n').split('\n');
  const rule = (l: string | undefined) => !!l && /^\s*={5,}\s*$/.test(l);
  const out: LicenseEntry[] = [];
  let current: { name: string; body: string[] } | null = null;
  for (let i = 0; i < lines.length; i++) {
    if (rule(lines[i]) && rule(lines[i + 2]) && lines[i + 1]?.trim()) {
      if (current) out.push({ name: current.name, text: current.body.join('\n').trim() });
      current = { name: lines[i + 1].trim(), body: [] };
      i += 2;
    } else if (current) {
      current.body.push(lines[i]);
    }
  }
  if (current) out.push({ name: current.name, text: current.body.join('\n').trim() });
  return out;
}

/** Libraries bundled into this web interface (the file above covers the service and the legacy interface). */
export const UI_LIBRARIES: ReadonlyArray<{ name: string; license: string; url: string }> = [
  { name: 'React', license: 'MIT', url: 'https://react.dev' },
  { name: 'Lucide icons', license: 'ISC', url: 'https://lucide.dev' },
  { name: 'Tailwind CSS', license: 'MIT', url: 'https://tailwindcss.com' },
];
