import { bugReportUrl, MAX_REPORT_LENGTH, REPOSITORY_URL } from '../help';

describe('bugReportUrl', () => {
  it('opens the bug form with the version and the system report filled in', () => {
    const url = new URL(bugReportUrl('0.2.0', 'Backbloom 0.2.0\nWindows 11 & co'));
    expect(`${url.origin}${url.pathname}`).toBe(`${REPOSITORY_URL}/issues/new`);
    expect(url.searchParams.get('template')).toBe('bug_report.yml');
    expect(url.searchParams.get('labels')).toBe('bug');
    expect(url.searchParams.get('version')).toBe('0.2.0');
    expect(url.searchParams.get('system')).toBe('Backbloom 0.2.0\nWindows 11 & co');
  });

  it('leaves out what is unknown', () => {
    const url = new URL(bugReportUrl(undefined, ''));
    expect(url.searchParams.has('version')).toBe(false);
    expect(url.searchParams.has('system')).toBe(false);
  });

  it('cuts a long system report so the link stays within what GitHub accepts', () => {
    const url = bugReportUrl('0.2.0', 'x'.repeat(10_000));
    const system = new URL(url).searchParams.get('system')!;
    expect(system).toHaveLength(MAX_REPORT_LENGTH + 2);
    expect(system.endsWith('\n…')).toBe(true);
    expect(url.length).toBeLessThan(8000);
  });
});
