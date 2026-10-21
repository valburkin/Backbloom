import { CheckCircle2, CircleAlert, Info, TriangleAlert } from 'lucide-react';
import type { LayoutIssue, LayoutSpec } from '../../layout';
import type { SaveBlocker } from '../../api/layoutService';
import { Card, cx } from '../../components/ui';

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

  return (
    <Card className="p-4">
      <h2 className="mb-2 text-sm font-semibold">Layout check</h2>
      {nonError.map((b) => (
        <p key={b.reason} className="mb-2 flex gap-2 rounded-lg border border-warn/30 bg-warn/10 p-2.5 text-[13px] text-warn">
          <Info size={15} className="mt-px shrink-0" />
          {b.message}
        </p>
      ))}
      {validatorError ? (
        <p className="text-[13px] text-ink-faint">The layout check isn’t available right now ({validatorError}).</p>
      ) : sorted.length === 0 ? (
        <p className="flex items-center gap-2 text-[13px] text-ok">
          <CheckCircle2 size={15} /> Everything looks good.
        </p>
      ) : (
        <ul className="space-y-1">
          {sorted.map((issue, i) => {
            const Icon = issue.severity === 'error' ? CircleAlert : TriangleAlert;
            const where = issue.room ? 'Room' : stripName(issue.stripId) ?? '';
            return (
              <li key={i}>
                <button
                  type="button"
                  disabled={!issue.stripId && !issue.room}
                  onClick={() => onPick(issue)}
                  className="flex w-full gap-2 rounded-lg px-2 py-1.5 text-left text-[13px] hover:bg-surface-2 disabled:hover:bg-transparent"
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
