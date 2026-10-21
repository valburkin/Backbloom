import { describe, expect, it } from 'vitest';
import { compileLayout, compileZones } from './compile';
import { applyProfile } from './profiles';
import { validateLayout } from './validate';
import type { CompiledController, LayoutSpec } from './types';
import specJson from '../../../backend/tests/fixtures/layouts/ui-multi-controller.spec.json';
import compiledJson from '../../../backend/tests/fixtures/layouts/ui-multi-controller.compiled.json';
import zonesJson from '../../../backend/tests/fixtures/layouts/ui-multi-controller.zones.json';
import witcherCompiled from '../../../backend/tests/fixtures/layouts/ui-multi-controller.witcher.compiled.json';
import witcherZones from '../../../backend/tests/fixtures/layouts/ui-multi-controller.witcher.zones.json';

// The backend compiles the saved layout itself (LayoutSpecification.Compile); its test compares the result with the
// same file (within 1e-4, one step of the stored precision, in case the two runtimes' floating point ever differs in
// the last bit), so both compilers produce these rects. Regenerate the file from compileLayout when the compiler changes.
describe('shared fixture with the backend (three controllers, strips on five surfaces)', () => {
  const spec = specJson as unknown as LayoutSpec;

  it('compiles to the rects the backend fixture holds', () => {
    expect(compileLayout(spec)).toEqual(compiledJson as unknown as CompiledController[]);
  });

  it('has the zones the backend fixture holds: one effect across two controllers, one color', () => {
    expect(compileZones(spec)).toEqual(zonesJson);
  });

  it('compiles the "witcher" profile to the rects and zones the backend fixture holds', () => {
    const witcher = applyProfile(spec, 'witcher');
    expect(compileLayout(witcher)).toEqual(witcherCompiled);
    expect(compileZones(witcher)).toEqual(witcherZones);
  });

  it('is valid (the shelf side strip leaves 3 LEDs of its output dark, and "Movies" was made for an older ceiling, on purpose)', () => {
    expect(validateLayout(spec).map((i) => i.severity)).toEqual(['warning', 'warning']);
  });
});
