// SPDX-License-Identifier: AGPL-3.0-or-later
// Copyright (c) 2026 The LiveCore Platform contributors

/**
 * Compile-time type tests for @livecore/design-tokens (CORE-SDK-003).
 *
 * These assertions are verified by `tsc` against `tsconfig.test.json`: a failed
 * assertion is a type error, so the package fails to type-check (and the package
 * `test` script fails). The file has no runtime behavior — it is types only — so
 * it is intentionally NOT a Node test file; the runtime/package-build checks live
 * in `design-tokens.build.test.mjs`.
 */
import type {
  ColorRole,
  ColorScheme,
  ColorValue,
  FontWeightValue,
  RadiusStep,
  SpacingStep,
  Theme,
  ThemeTokens,
} from "../src/index.js";
import { VERSION, baseTheme, defineTheme } from "../src/index.js";

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

// --- Scale unions are the exact stable key set. --------------------------------

export type ColorSchemeIsExact = Assert<Equal<ColorScheme, "light" | "dark">>;

export type ColorRoleIsExact = Assert<
  Equal<
    ColorRole,
    | "background"
    | "surface"
    | "overlay"
    | "foreground"
    | "muted"
    | "border"
    | "primary"
    | "primaryForeground"
    | "secondary"
    | "secondaryForeground"
    | "accent"
    | "accentForeground"
    | "success"
    | "warning"
    | "danger"
    | "info"
  >
>;

export type SpacingStepIsExact = Assert<
  Equal<SpacingStep, "none" | "xs" | "sm" | "md" | "lg" | "xl" | "2xl" | "3xl">
>;

export type RadiusStepIsExact = Assert<
  Equal<RadiusStep, "none" | "sm" | "md" | "lg" | "full">
>;

// --- The theme contract has exactly the documented token categories. ----------

export type ThemeTokensKeys = Assert<
  Equal<
    keyof ThemeTokens,
    | "color"
    | "spacing"
    | "typography"
    | "radius"
    | "shadow"
    | "breakpoint"
    | "motion"
  >
>;

// --- Token records are keyed by the scale, with the right value type. ---------

export type ColorRecordIsKeyedByRole = Assert<
  Equal<keyof ThemeTokens["color"]["light"], ColorRole>
>;

export type ColorValueIsString = Assert<
  Equal<ThemeTokens["color"]["light"]["primary"], ColorValue>
>;

export type FontWeightIsNumber = Assert<
  Equal<ThemeTokens["typography"]["fontWeight"]["bold"], FontWeightValue>
>;

// --- A full theme literal satisfies the contract; the base theme is a Theme. ---

export type BaseThemeIsTheme = Assert<Equal<typeof baseTheme, Theme>>;

export type DefineThemeReturnsTheme = Assert<
  Equal<ReturnType<typeof defineTheme>, Theme>
>;

// The base theme is a structurally valid Theme value (compile-time check).
export const baseThemeExample: Theme = baseTheme;

// A vertical re-skins only what it wants by spreading the base token set; the
// result still satisfies the full contract (no token may be dropped).
export const verticalThemeExample: Theme = defineTheme({
  name: "vertical-example",
  tokens: {
    ...baseTheme.tokens,
    color: {
      ...baseTheme.tokens.color,
      light: {
        ...baseTheme.tokens.color.light,
        primary: "#111827",
        primaryForeground: "#f9fafb",
      },
    },
  },
});
