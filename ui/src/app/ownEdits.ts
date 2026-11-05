import type { LayoutSpec } from '../layout';

/**
 * The layout edits a page saves by itself (Presets): saved shortly after the last one, and at once when the page is
 * left (`flush`). While its own edits aren't saved the page counts the unsaved layout as its own (`own`); edits that
 * aren't its own (Layout's) block it instead. Each edit is counted, so a save that ends while newer edits wait leaves
 * the layout the page's own.
 */
export class OwnEdits {
  /** The unsaved edits are this page's. */
  own = false;
  /** A layout whose save failed: not saved again by itself (the next edit tries). */
  failed: LayoutSpec | null = null;
  private count = 0;
  private pending: { spec: LayoutSpec; edit: number } | null = null;
  private timer: ReturnType<typeof setTimeout> | null = null;

  constructor(
    private readonly save: (spec: LayoutSpec) => Promise<boolean>,
    private readonly delay = 400,
  ) {}

  /** An edit made here (before it is dispatched); its number, for `saved`. */
  edit(): number {
    this.own = true;
    return ++this.count;
  }

  /** The layout with the edits up to `edit` was saved: the page's own edits are done unless newer ones came since. */
  saved(edit: number): void {
    if (edit === this.count) this.own = false;
  }

  /** The edited layout to save shortly (the edits so far); replaces one still waiting. */
  schedule(spec: LayoutSpec): void {
    this.cancel();
    this.pending = { spec, edit: this.count };
    this.timer = setTimeout(() => this.flush(), this.delay);
  }

  /** Nothing to save any more (saved by other means, or not the page's). */
  cancel(): void {
    if (this.timer) clearTimeout(this.timer);
    this.timer = null;
    this.pending = null;
  }

  /** Saves what waits now (the page is left). */
  flush(): void {
    const p = this.pending;
    this.cancel();
    if (!p) return;
    void this.save(p.spec).then((ok) => {
      if (ok) this.saved(p.edit);
      else this.failed = p.spec;
    });
  }
}
