// ESLint flat config for the TypeScript workspace packages (CORE-FND-002).
//
// Minimal standard setup: @eslint/js recommended rules plus the
// typescript-eslint recommended rules. Type-aware linting is intentionally
// not enabled yet; every package build already runs 'tsc' in strict mode.
import js from "@eslint/js";
import tseslint from "typescript-eslint";

export default tseslint.config(
  {
    ignores: ["**/dist/", "**/node_modules/", "**/bin/", "**/obj/"],
  },
  {
    files: ["**/*.{ts,tsx,mts,cts}"],
    extends: [js.configs.recommended, tseslint.configs.recommended],
  },
);
