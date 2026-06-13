/**
 * Variant resolution (CORE-SDK-004): the small, pure, framework-agnostic logic
 * that fills a partially-specified primitive variant with Core's stable
 * defaults. This is ui-core's stable runtime surface — parallel to how
 * @livecore/design-tokens ships `baseTheme` and `defineTheme` — so every
 * vertical renderer resolves a primitive's tone, size and emphasis identically
 * and the defaults never drift between verticals.
 *
 * It is pure data logic: no theme values, no element and no framework. A
 * vertical takes the resolved variant plus its own `Theme`
 * (@livecore/design-tokens) and produces the actual styled element.
 */
import {
  DEFAULT_EMPHASIS,
  DEFAULT_SIZE,
  DEFAULT_TONE,
  type Emphasis,
  type Size,
  type Tone,
} from "./variants.js";

/** The tone/size/emphasis a primitive may specify, each optional. */
export interface VariantSelection {
  readonly tone?: Tone;
  readonly size?: Size;
  readonly emphasis?: Emphasis;
}

/**
 * A fully-resolved variant: every field decided, with Core's stable defaults
 * applied for anything the caller left unset.
 */
export interface ResolvedVariant {
  readonly tone: Tone;
  readonly size: Size;
  readonly emphasis: Emphasis;
}

/** Core's stable default variant (every field at its documented default). */
export const DEFAULT_VARIANT: ResolvedVariant = {
  tone: DEFAULT_TONE,
  size: DEFAULT_SIZE,
  emphasis: DEFAULT_EMPHASIS,
};

/**
 * Resolve a partial variant selection into a complete `ResolvedVariant` by
 * applying Core's stable defaults for any unset field. Pure: it returns a new
 * object and never mutates its argument, so the same selection always resolves
 * the same way.
 */
export function resolveVariant(
  selection: VariantSelection = {},
): ResolvedVariant {
  return {
    tone: selection.tone ?? DEFAULT_TONE,
    size: selection.size ?? DEFAULT_SIZE,
    emphasis: selection.emphasis ?? DEFAULT_EMPHASIS,
  };
}
