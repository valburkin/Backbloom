import { useEffect } from 'react';
import { Loader2, Redo2, RotateCcw, Save, Undo2 } from 'lucide-react';
import { Button, IconButton } from './ui';
import { useEditor } from '../app/editor';
import { useConnection } from '../app/connection';
import { canRedo, canUndo } from '../editor/state';

function isTextTarget(t: EventTarget | null): boolean {
  if (!(t instanceof HTMLElement)) return false;
  if (t.isContentEditable || t.tagName === 'TEXTAREA') return true;
  return t.tagName === 'INPUT' && ['text', 'password', 'number', 'search', ''].includes((t as HTMLInputElement).type);
}

/** Undo / redo / unsaved indicator / Save. Save only ever runs from the button. */
export function SaveBar() {
  const { state, dispatch, dirty, save, saving, blockers, reload, load } = useEditor();
  const { auth } = useConnection();

  useEffect(() => {
    const onKey = (e: KeyboardEvent) => {
      if (!(e.ctrlKey || e.metaKey) || isTextTarget(e.target)) return;
      const k = e.key.toLowerCase();
      if (k === 'z' && !e.shiftKey) {
        e.preventDefault();
        dispatch({ type: 'undo' });
      } else if (k === 'y' || (k === 'z' && e.shiftKey)) {
        e.preventDefault();
        dispatch({ type: 'redo' });
      }
    };
    window.addEventListener('keydown', onKey);
    return () => window.removeEventListener('keydown', onKey);
  }, [dispatch]);

  const blocked = blockers.length > 0;
  const signedIn = auth === 'admin';
  const reason = !signedIn ? 'Sign in to save' : blocked ? blockers[0].message : !dirty ? 'No changes to save' : undefined;

  return (
    <div className="flex items-center gap-1.5">
      <IconButton label="Undo (Ctrl+Z)" disabled={!canUndo(state)} onClick={() => dispatch({ type: 'undo' })}>
        <Undo2 size={17} />
      </IconButton>
      <IconButton label="Redo (Ctrl+Y)" disabled={!canRedo(state)} onClick={() => dispatch({ type: 'redo' })}>
        <Redo2 size={17} />
      </IconButton>
      <IconButton
        label="Discard changes"
        disabled={load.status === 'loading'}
        onClick={() => {
          if (!dirty || window.confirm('Discard your unsaved changes and reload the layout?')) reload();
        }}
      >
        <RotateCcw size={16} />
      </IconButton>
      <span className="mx-1 h-5 w-px bg-line" aria-hidden />
      <span className="min-w-[7.5rem] text-right text-[13px]" aria-live="polite">
        {dirty ? (
          <span className="inline-flex items-center gap-1.5 text-warn">
            <span className="h-1.5 w-1.5 rounded-full bg-warn" aria-hidden />
            Unsaved changes
          </span>
        ) : (
          <span className="text-ink-faint">All changes saved</span>
        )}
      </span>
      <Button
        variant={dirty || !signedIn ? 'primary' : 'secondary'}
        size="sm"
        className="ml-1"
        disabled={saving || (signedIn && (blocked || !dirty))}
        title={reason}
        onClick={() => void save()}
        icon={saving ? <Loader2 size={15} className="animate-spin" /> : <Save size={15} />}
      >
        {signedIn ? (saving ? 'Saving…' : 'Save') : 'Sign in to save'}
      </Button>
    </div>
  );
}
