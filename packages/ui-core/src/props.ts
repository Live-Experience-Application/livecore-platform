// SPDX-License-Identifier: AGPL-3.0-or-later
// Copyright (c) 2026 The LiveCore Platform contributors

/**
 * The generic UI primitive prop contracts (CORE-SDK-004): the typed, stable
 * shape of the props each Core primitive accepts. Core owns this contract — the
 * primitives and the props within each (drawn from the variant vocabularies in
 * `variants.ts`) — while a vertical owns the rendering: it implements a real
 * element (typically a React component, but the contract is framework-agnostic)
 * that accepts these props and applies its theme (@livecore/design-tokens).
 *
 * The props are deliberately framework-agnostic data — no element type, event
 * handler or DOM node — so the contract is stable across rendering frameworks and
 * carries no runtime dependency, mirroring how the sibling Core packages stay
 * types-first. The vocabulary is generic UI purpose names only and carries no
 * vertical domain language (AGENTS.md; docs/04_PRODUCT_BOUNDARIES.md).
 */
import type {
  Alignment,
  Emphasis,
  Justification,
  Orientation,
  Size,
  SurfaceLevel,
  Tone,
} from "./variants.js";

/**
 * Props common to every Core UI primitive. These are plain data hooks a
 * vertical's renderer maps onto its framework (for example `testId` becomes a
 * `data-testid` attribute); none of them is a rendered element or a handler.
 */
export interface BasePrimitiveProps {
  /** Stable element id, for labelling or in-page anchoring. */
  readonly id?: string;
  /** Test hook id a vertical surfaces (e.g. as `data-testid`). */
  readonly testId?: string;
  /** Extra class hook(s) a vertical stylesheet can target. */
  readonly className?: string;
  /** When true the primitive is present but not displayed. */
  readonly hidden?: boolean;
}

/** A container surface: a toned, elevated region that groups other primitives. */
export interface SurfaceProps extends BasePrimitiveProps {
  readonly level?: SurfaceLevel;
  readonly tone?: Tone;
  readonly padding?: Size;
}

/** A one-dimensional layout that arranges its children along a single axis. */
export interface StackProps extends BasePrimitiveProps {
  readonly orientation?: Orientation;
  readonly gap?: Size;
  readonly align?: Alignment;
  readonly justify?: Justification;
}

/** Generic font-weight roles a text primitive may request. */
export const TextWeights = ["regular", "medium", "semibold", "bold"] as const;

/** A text weight name. */
export type TextWeight = (typeof TextWeights)[number];

/** Inline or block running text. */
export interface TextProps extends BasePrimitiveProps {
  readonly size?: Size;
  readonly tone?: Tone;
  readonly weight?: TextWeight;
  /** When true, overflowing text is truncated with an ellipsis on one line. */
  readonly truncate?: boolean;
}

/** The document heading levels a heading primitive may render at. */
export const HeadingLevels = [1, 2, 3, 4, 5, 6] as const;

/** A heading level. */
export type HeadingLevel = (typeof HeadingLevels)[number];

/** A section heading. `level` is the semantic document level (1-6). */
export interface HeadingProps extends BasePrimitiveProps {
  readonly level: HeadingLevel;
  readonly size?: Size;
  readonly tone?: Tone;
}

/** The native button behaviors a button primitive may declare. */
export const ButtonTypes = ["button", "submit", "reset"] as const;

/** A button type. */
export type ButtonType = (typeof ButtonTypes)[number];

/** An action trigger. */
export interface ButtonProps extends BasePrimitiveProps {
  readonly tone?: Tone;
  readonly size?: Size;
  readonly emphasis?: Emphasis;
  readonly type?: ButtonType;
  readonly disabled?: boolean;
  /** When true the action is in progress; the renderer shows a busy state. */
  readonly loading?: boolean;
}

/** A small status or category marker. */
export interface BadgeProps extends BasePrimitiveProps {
  readonly tone?: Tone;
  readonly emphasis?: Emphasis;
  readonly size?: Size;
}

/** A labelled form control wrapper (label, description and validity state). */
export interface FieldProps extends BasePrimitiveProps {
  readonly label: string;
  readonly size?: Size;
  readonly required?: boolean;
  readonly invalid?: boolean;
  readonly description?: string;
}

/** A busy indicator. */
export interface SpinnerProps extends BasePrimitiveProps {
  readonly size?: Size;
  readonly tone?: Tone;
  /** Accessible label announced by assistive technology. */
  readonly label?: string;
}

/** A separating line between content. */
export interface DividerProps extends BasePrimitiveProps {
  readonly orientation?: Orientation;
}

/** A compact representation of a user or entity. */
export interface AvatarProps extends BasePrimitiveProps {
  readonly size?: Size;
  /** Display name used for the fallback initials and the accessible label. */
  readonly name?: string;
}
