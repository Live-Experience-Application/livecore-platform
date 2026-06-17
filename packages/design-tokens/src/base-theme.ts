// SPDX-License-Identifier: AGPL-3.0-or-later
// Copyright (c) 2026 The LiveCore Platform contributors

/**
 * The Core base theme (CORE-SDK-003): a complete, neutral set of design tokens
 * that satisfies the `Theme` contract. It exists so a vertical app can render
 * usable, accessible Core UI primitives immediately and then override only the
 * tokens it wants to re-skin — Core ships the contract and a sane default, a
 * vertical ships its brand (AGENTS.md; docs/04_PRODUCT_BOUNDARIES.md).
 *
 * The palette is intentionally generic (neutral grays with a restrained blue
 * primary, violet accent and conventional status hues); it carries no vertical
 * visual identity. The values are plain CSS strings/numbers so they drop
 * straight into a stylesheet, CSS custom properties or inline styles.
 */
import { defineTheme, type Theme } from "./tokens.js";

/**
 * The generic Core default theme. Defined through `defineTheme`, so the compiler
 * verifies it provides every token the contract requires.
 */
export const baseTheme: Theme = defineTheme({
  name: "livecore-base",
  tokens: {
    color: {
      light: {
        background: "#ffffff",
        surface: "#f8fafc",
        overlay: "rgba(15, 23, 42, 0.5)",
        foreground: "#0f172a",
        muted: "#64748b",
        border: "#e2e8f0",
        primary: "#2563eb",
        primaryForeground: "#ffffff",
        secondary: "#475569",
        secondaryForeground: "#ffffff",
        accent: "#7c3aed",
        accentForeground: "#ffffff",
        success: "#16a34a",
        warning: "#d97706",
        danger: "#dc2626",
        info: "#0284c7",
      },
      dark: {
        background: "#0f172a",
        surface: "#1e293b",
        overlay: "rgba(2, 6, 23, 0.6)",
        foreground: "#f8fafc",
        muted: "#94a3b8",
        border: "#334155",
        primary: "#3b82f6",
        primaryForeground: "#0f172a",
        secondary: "#cbd5e1",
        secondaryForeground: "#0f172a",
        accent: "#a78bfa",
        accentForeground: "#0f172a",
        success: "#22c55e",
        warning: "#f59e0b",
        danger: "#ef4444",
        info: "#38bdf8",
      },
    },
    spacing: {
      none: "0",
      xs: "0.25rem",
      sm: "0.5rem",
      md: "1rem",
      lg: "1.5rem",
      xl: "2rem",
      "2xl": "3rem",
      "3xl": "4rem",
    },
    typography: {
      fontFamily: {
        sans: 'system-ui, -apple-system, "Segoe UI", Roboto, Helvetica, Arial, sans-serif',
        serif: 'Georgia, Cambria, "Times New Roman", Times, serif',
        mono: 'ui-monospace, SFMono-Regular, "Cascadia Code", "Source Code Pro", Menlo, monospace',
      },
      fontSize: {
        xs: "0.75rem",
        sm: "0.875rem",
        md: "1rem",
        lg: "1.125rem",
        xl: "1.5rem",
        "2xl": "2rem",
        "3xl": "2.5rem",
      },
      fontWeight: {
        regular: 400,
        medium: 500,
        semibold: 600,
        bold: 700,
      },
      lineHeight: {
        tight: 1.25,
        normal: 1.5,
        relaxed: 1.75,
      },
    },
    radius: {
      none: "0",
      sm: "0.25rem",
      md: "0.5rem",
      lg: "1rem",
      full: "9999px",
    },
    shadow: {
      none: "none",
      sm: "0 1px 2px 0 rgba(15, 23, 42, 0.08)",
      md: "0 4px 6px -1px rgba(15, 23, 42, 0.1)",
      lg: "0 10px 15px -3px rgba(15, 23, 42, 0.12)",
    },
    breakpoint: {
      sm: "640px",
      md: "768px",
      lg: "1024px",
      xl: "1280px",
    },
    motion: {
      duration: {
        instant: "0ms",
        fast: "120ms",
        normal: "200ms",
        slow: "320ms",
      },
      easing: {
        standard: "cubic-bezier(0.2, 0, 0, 1)",
        accelerate: "cubic-bezier(0.3, 0, 1, 1)",
        decelerate: "cubic-bezier(0, 0, 0, 1)",
      },
    },
  },
});
