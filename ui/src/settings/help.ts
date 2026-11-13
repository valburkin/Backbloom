/** Where people get help: the repository's issues (bugs) and discussions (questions and ideas). */
export const REPOSITORY_URL = 'https://github.com/valburkin/Backbloom';
export const DISCUSSIONS_URL = `${REPOSITORY_URL}/discussions`;
export const KNOWN_ISSUES_URL = `${REPOSITORY_URL}/issues?q=is%3Aissue+is%3Aopen+label%3Abug`;

/** GitHub refuses very long URLs; the system report is cut to this length (the form asks for the rest). */
export const MAX_REPORT_LENGTH = 1500;

/**
 * The bug report form (.github/ISSUE_TEMPLATE/bug_report.yml) filled in with the version and the system report:
 * GitHub prefills issue form fields from query parameters named by the fields' ids.
 */
export function bugReportUrl(version: string | undefined, systemReport: string): string {
  const report = systemReport.length > MAX_REPORT_LENGTH ? `${systemReport.slice(0, MAX_REPORT_LENGTH)}\n…` : systemReport;
  const params = new URLSearchParams({ template: 'bug_report.yml', labels: 'bug' });
  if (version) params.set('version', version);
  if (report) params.set('system', report);
  return `${REPOSITORY_URL}/issues/new?${params.toString()}`;
}
