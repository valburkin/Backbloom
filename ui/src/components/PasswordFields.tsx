import { Input } from './form';
import { cx } from './ui';
import { passwordStrength, STRENGTH_TEXT, type PasswordStrength } from '../settings/password';

const BARS: Record<PasswordStrength, { filled: number; tone: string }> = {
  short: { filled: 0, tone: 'bg-line' },
  weak: { filled: 1, tone: 'bg-bad' },
  fair: { filled: 2, tone: 'bg-warn' },
  strong: { filled: 3, tone: 'bg-ok' },
};

/** New password + confirmation with a strength hint (the remote access dialog and Settings → Remote access). */
export function NewPasswordFields({
  password,
  confirm,
  onPassword,
  onConfirm,
  label = 'New password',
  className,
}: {
  password: string;
  confirm: string;
  onPassword(v: string): void;
  onConfirm(v: string): void;
  label?: string;
  className?: string;
}) {
  const strength = passwordStrength(password);
  const bars = BARS[strength];
  const mismatch = confirm.length > 0 && confirm !== password;
  return (
    <div className={cx('grid gap-4 sm:grid-cols-2', className)}>
      <div className="space-y-1.5">
        <Input label={label} type="password" autoComplete="new-password" value={password} onChange={onPassword} />
        <div className="flex gap-1" aria-hidden>
          {[0, 1, 2].map((i) => (
            <span key={i} className={cx('h-1 flex-1 rounded-full', i < bars.filled ? bars.tone : 'bg-line')} />
          ))}
        </div>
        <p className="text-xs text-ink-faint" aria-live="polite">
          {password.length === 0 ? STRENGTH_TEXT.short : STRENGTH_TEXT[strength]}
        </p>
      </div>
      <Input
        label="Repeat the password"
        type="password"
        autoComplete="new-password"
        value={confirm}
        onChange={onConfirm}
        error={mismatch ? 'The passwords don’t match.' : undefined}
      />
    </div>
  );
}
