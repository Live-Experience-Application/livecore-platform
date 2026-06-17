// SPDX-License-Identifier: AGPL-3.0-or-later
// Copyright (c) 2026 The LiveCore Platform contributors

/**
 * Compile-time type tests for @livecore/ui-core (CORE-SDK-004).
 *
 * These assertions are verified by `tsc` against `tsconfig.test.json`: a failed
 * assertion is a type error, so the package fails to type-check (and the package
 * `test` script fails). The file has no runtime behavior — it is types only — so
 * it is intentionally NOT a Node test file; the runtime/package-build checks live
 * in `ui-core.build.test.mjs`.
 */
import type {
  AvatarProps,
  ButtonProps,
  ButtonType,
  Emphasis,
  HeadingLevel,
  PrimitiveKind,
  ResolvedVariant,
  Size,
  SurfaceLevel,
  TextWeight,
  Tone,
  VariantSelection,
} from "../src/index.js";
import { DEFAULT_VARIANT, VERSION, resolveVariant } from "../src/index.js";

/** `true` only when `X` and `Y` are the exact same type. */
type Equal<X, Y> =
  (<T>() => T extends X ? 1 : 2) extends <T>() => T extends Y ? 1 : 2
    ? true
    : false;

/** Compiles only when its argument resolves to the literal `true`. */
type Assert<T extends true> = T;

// --- The exported VERSION is a stable, well-formed SemVer string literal. -------
// (CORE-SDK-005.) The version is part of the package's typed surface, so a
// consumer can rely on its shape at compile time; the runtime agreement between
// VERSION, package.json and CHANGELOG.md is checked by the package-build test.

/** `true` only when `V` is a literal type, not the widened `string`. */
type IsStringLiteral<V extends string> = string extends V ? false : true;

/** `true` only when `V` has the SemVer `MAJOR.MINOR.PATCH` core shape. */
type IsSemanticVersion<V extends string> =
  V extends `${number}.${number}.${number}` ? true : false;

export type VersionIsStringLiteral = Assert<IsStringLiteral<typeof VERSION>>;
export type VersionIsSemanticVersion = Assert<
  IsSemanticVersion<typeof VERSION>
>;

// --- Variant unions are the exact stable key set. ------------------------------

export type ToneIsExact = Assert<
  Equal<
    Tone,
    | "neutral"
    | "primary"
    | "secondary"
    | "accent"
    | "success"
    | "warning"
    | "danger"
    | "info"
  >
>;

export type SizeIsExact = Assert<Equal<Size, "sm" | "md" | "lg">>;

export type EmphasisIsExact = Assert<
  Equal<Emphasis, "solid" | "soft" | "outline" | "ghost">
>;

export type SurfaceLevelIsExact = Assert<
  Equal<SurfaceLevel, "base" | "raised" | "overlay">
>;

export type TextWeightIsExact = Assert<
  Equal<TextWeight, "regular" | "medium" | "semibold" | "bold">
>;

export type ButtonTypeIsExact = Assert<
  Equal<ButtonType, "button" | "submit" | "reset">
>;

export type HeadingLevelIsExact = Assert<
  Equal<HeadingLevel, 1 | 2 | 3 | 4 | 5 | 6>
>;

export type PrimitiveKindIsExact = Assert<
  Equal<
    PrimitiveKind,
    | "surface"
    | "stack"
    | "text"
    | "heading"
    | "button"
    | "badge"
    | "field"
    | "spinner"
    | "divider"
    | "avatar"
  >
>;

// --- Prop contracts draw from the variant vocabulary. --------------------------

export type ButtonToneIsTone = Assert<
  Equal<NonNullable<ButtonProps["tone"]>, Tone>
>;

export type ButtonEmphasisIsEmphasis = Assert<
  Equal<NonNullable<ButtonProps["emphasis"]>, Emphasis>
>;

// Every primitive prop type extends the shared base props.
export type ButtonHasTestId = Assert<
  Equal<NonNullable<ButtonProps["testId"]>, string>
>;

export type AvatarHasTestId = Assert<
  Equal<NonNullable<AvatarProps["testId"]>, string>
>;

// --- The resolver fully decides every variant field. ---------------------------

export type ResolveVariantReturnsResolved = Assert<
  Equal<ReturnType<typeof resolveVariant>, ResolvedVariant>
>;

export type ResolvedVariantHasNoOptionalFields = Assert<
  Equal<keyof ResolvedVariant, "tone" | "size" | "emphasis">
>;

// A partial selection is accepted; the call site is type-checked.
export const partialSelection: VariantSelection = { tone: "primary" };
export const resolvedFromPartial: ResolvedVariant =
  resolveVariant(partialSelection);

// Calling with no argument is valid and still yields a complete variant.
export const resolvedFromNothing: ResolvedVariant = resolveVariant();

// The exported default is a fully-resolved variant value.
export const defaultVariantExample: ResolvedVariant = DEFAULT_VARIANT;

// A primitive props literal type-checks against its contract.
export const buttonExample: ButtonProps = {
  testId: "primary-action",
  tone: "primary",
  size: "lg",
  emphasis: "solid",
  type: "submit",
  loading: false,
};
