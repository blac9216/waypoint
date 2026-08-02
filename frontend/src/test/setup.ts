import "@testing-library/jest-dom/vitest";
import { cleanup } from "@testing-library/react";
import { afterEach } from "vitest";

// vitest.config.ts doesn't set test.globals — RTL's auto-cleanup relies on
// detecting a global afterEach, so wire it explicitly instead.
afterEach(() => {
	cleanup();
});
