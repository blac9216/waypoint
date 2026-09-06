import react from "@vitejs/plugin-react";
import { defineConfig } from "vitest/config";

export default defineConfig({
	plugins: [react()],
	test: {
		environment: "jsdom",
		setupFiles: ["./src/test/setup.ts"],
		css: true,
		// e2e/ holds Playwright specs (npm run test:e2e), which run against a
		// real browser + live stack, not jsdom — vitest must never try to
		// collect them (they use @playwright/test's own test()/expect()).
		exclude: ["e2e/**", "node_modules/**"],
		coverage: {
			provider: "v8",
			// "json-summary" is built into @vitest/coverage-v8 (already vendored,
			// no new dependency) and is what the CI coverage-floor gate parses
			// (scripts/check-coverage-floor.py --format vitest-json-summary).
			// "text"/"html" remain for humans reading a local run or the
			// uploaded CI artifact.
			reporter: ["text", "html", "json-summary"],
			include: ["src/**/*.{ts,tsx}"],
			// src/screens/** used to be excluded here, which meant the CI floor
			// gate could not see the largest and fastest-changing part of the
			// frontend — a PR could add 200 lines of uncovered screen code and
			// the reported percentage would not move (issue #1314). Screens are
			// now included; the 88.0% floor in .github/workflows/frontend.yml
			// was re-measured with them in (base 93.37% screens-excluded, 88.57%
			// screens-included) and still holds without a change.
			exclude: ["src/main.tsx", "src/vite-env.d.ts", "src/**/*.test.{ts,tsx}"],
		},
	},
});
