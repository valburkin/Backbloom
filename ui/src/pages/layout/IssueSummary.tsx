import { CircleAlert, Info, TriangleAlert } from 'lucide-react';
import type { LayoutIssue, LayoutSpec } from '../../layout';
import type { SaveBlocker } from '../../api/layoutService';
import { Card, cx } from '../../components/ui';

/** "Problems": what keeps the layout from being saved or working well; nothing when all is fine. */
export function IssueSummary({
  spec,
  issues,
  validatorError,
  blockers,
  onPick,
}: {
  spec: LayoutSpec;
  issues: LayoutIssue[];
  validatorError: string | null;
  blockers: SaveBlocker[];
  onPick: (issue: LayoutIssue) => void;
}) {
  const nonError = blockers.filter((b) => b.reason !== 'errors');
  const sorted = [...issues].sort((a, b) => Number(b.severity === 'error') - Number(a.severity === 'error'));
  const stripName = (id?: string) => spec.strips.find((s) => s.id === id)?.name;
  if (!validatorError && sorted.length === 0 && nonError.length === 0) return null;

  return (
    <Card className="p-4">
      <h2 className="mb-2 text-base font-semibold">Problems</h2>
      {nonError.map((b) => (
        <p key={b.reason} className="mb-2 flex gap-2 text-[13px] text-warn">
          <Info size={15} className="mt-px shrink-0" />
          {b.message}
        </p>
      ))}
      {validatorError ? (
        <p className="text-[13px] text-ink-faint">Couldn’t check the layout ({validatorError}).</p>
      ) : (
        <ul className="-mx-2 space-y-0.5">
          {sorted.map((issue, i) => {
            const Icon = issue.severity === 'error' ? CircleAlert : TriangleAlert;
            const where = issue.room ? 'Room' : stripName(issue.stripId) ?? '';
            return (
              <li key={i}>
                <button
                  type="button"
                  disabled={!issue.stripId && !issue.room}
                  onClick={() => onPick(issue)}
                  className="flex w-full gap-2 rounded-lg px-2 py-1.5 text-left text-[13px] hover:bg-white/[0.04] disabled:hover:bg-transparent"
                >
                  <Icon size={15} className={cx('mt-px shrink-0', issue.severity === 'error' ? 'text-bad' : 'text-warn')} />
                  <span>
                    {where && <span className="font-medium text-ink">{where}: </span>}
                    <span className="text-ink-muted">{issue.message}</span>
                  </span>
                </button>
              </li>
            );
          })}
        </ul>
      )}
    </Card>
  );
}
