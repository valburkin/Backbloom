import { describe, expect, it } from 'vitest';
import thirdParty from '../../../../3RD_PARTY_LICENSES?raw';
import { parseLicenses } from '../licenses';

describe('parseLicenses', () => {
  it('splits the repository license file into named entries', () => {
    const list = parseLicenses(thirdParty);
    expect(list.length).toBeGreaterThan(10);
    expect(list[0].name).toBe('AdminLTE');
    expect(list[0].text).toMatch(/^The MIT License/);
    expect(list.every((l) => l.name && l.text)).toBe(true);
    expect(list.some((l) => l.text.includes('=====') && /^\s*=+\s*$/m.test(l.text))).toBe(false);
  });

  it('handles CRLF and tolerates text before the first heading', () => {
    expect(parseLicenses('intro\r\n=====\r\n  A  \r\n=====\r\n\r\nalpha\r\n=====\r\nB\r\n=====\r\nbeta\r\n')).toEqual([
      { name: 'A', text: 'alpha' },
      { name: 'B', text: 'beta' },
    ]);
  });
});
