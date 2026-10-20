/** The backend's minimum (AuthService.MinPasswordLength). */
export const MIN_PASSWORD_LENGTH = 8;

export type PasswordStrength = 'short' | 'weak' | 'fair' | 'strong';

// Passwords people pick first; product names are the most guessable of all here.
const COMMON = ['password', 'overglow', 'hyperhdr', 'hyperion', '12345678', '123456789', 'qwertyui', 'qwerty123', 'iloveyou', 'abcdefgh', 'letmein1'];

/**
 * A rough guide, not a guarantee: length counts most, mixing letters, digits and symbols helps, and common or
 * repetitive passwords are weak whatever their length.
 */
export function passwordStrength(password: string): PasswordStrength {
  if (password.length < MIN_PASSWORD_LENGTH) return 'short';
  const lower = password.toLowerCase();
  if (COMMON.some((c) => lower.includes(c)) || new Set(password).size <= 3) return 'weak';
  const kinds = [/[a-z]/, /[A-Z]/, /\d/, /[^A-Za-z0-9]/].filter((r) => r.test(password)).length;
  if (password.length >= 16 || (password.length >= 12 && kinds >= 2)) return 'strong';
  if (kinds >= 3 || password.length >= 12) return 'fair';
  return 'weak';
}

export const STRENGTH_TEXT: Record<PasswordStrength, string> = {
  short: `Use at least ${MIN_PASSWORD_LENGTH} characters.`,
  weak: 'Weak: easy to guess. Make it longer, or mix in digits and symbols.',
  fair: 'Fair: a few more characters make it strong.',
  strong: 'Strong.',
};

/** Why a new password can't be saved yet, or null. */
export function newPasswordProblem(password: string, confirm: string): string | null {
  if (password.length < MIN_PASSWORD_LENGTH) return STRENGTH_TEXT.short;
  if (password !== confirm) return 'The passwords don’t match.';
  return null;
}
