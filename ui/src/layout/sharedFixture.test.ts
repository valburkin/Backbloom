import { describe, expect, it } from 'vitest';
import { compileLayout } from './compile';
import { validateLayout } from './validate';
import type { CompiledController, LayoutSpec } from './types';
import specJson from '../../../backend/tests/fixtures/layouts/ui-multi-controller.spec.json';
import compiledJson from '../../../backend/tests/fixtures/layouts/ui-multi-controller.compiled.json';

// The backend compiles the saved layout itself (LayoutSpecification.Compile); its test compares the result with the
// same file, so both compilers produce exactly these rects.
describe('shared fixture with the backend (three controllers)', () => {
  const spec = specJson as unknown as LayoutSpec;

  it('compiles to the rects the backend fixture holds', () => {
    expect(compileLayout(spec)).toEqual(compiledJson as unknown as CompiledController[]);
  });

  it('is valid (the shelf side strip leaves 3 LEDs of its output dark on purpose)', () => {
    expect(validateLayout(spec).map((i) => i.severity)).toEqual(['warning']);
  });
});
