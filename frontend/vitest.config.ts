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
			exclude: ["src/main.tsx", "src/vite-env.d.ts", "src/**/*.test.{ts,tsx}", "src/screens/**"],
		},
	},
});
