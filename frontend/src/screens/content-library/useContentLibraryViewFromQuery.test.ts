/**
 * useContentLibraryViewFromQuery — issue #1399 AC3. Proves the URL is the
 * source of truth for view state: reads seed from the query string on
 * mount, `setView` writes back via `pushState` (no reload), and unknown/
 * invalid values fall back to defaults rather than throwing.
 */
import { act, renderHook } from "@testing-library/react";
import { afterEach, beforeEach, describe, expect, it } from "vitest";
import { useContentLibraryViewFromQuery } from "./useContentLibraryViewFromQuery";

beforeEach(() => {
	window.history.pushState(null, "", "/content-library");
});

afterEach(() => {
	window.history.pushState(null, "", "/content-library");
});

describe("useContentLibraryViewFromQuery", () => {
	it("defaults to all/no-search/name-sort with no query string", () => {
		const { result } = renderHook(() => useContentLibraryViewFromQuery());
		expect(result.current.libraryId).toBeUndefined();
		expect(result.current.type).toBe("all");
		expect(result.current.search).toBe("");
		expect(result.current.sort).toBe("name");
	});

	it("seeds state from an existing query string", () => {
		window.history.pushState(null, "", "/content-library?library=lib-1&type=vcsp.iso&q=esxi&sort=size");
		const { result } = renderHook(() => useContentLibraryViewFromQuery());
		expect(result.current).toMatchObject({ libraryId: "lib-1", type: "vcsp.iso", search: "esxi", sort: "size" });
	});

	it("falls back to defaults for an invalid type/sort value rather than throwing", () => {
		window.history.pushState(null, "", "/content-library?type=bogus&sort=bogus");
		const { result } = renderHook(() => useContentLibraryViewFromQuery());
		expect(result.current.type).toBe("all");
		expect(result.current.sort).toBe("name");
	});

	it("setView updates state and the URL without a reload, merging with current values", () => {
		const { result } = renderHook(() => useContentLibraryViewFromQuery());

		act(() => result.current.setView({ libraryId: "lib-9" }));
		expect(result.current.libraryId).toBe("lib-9");
		expect(new URLSearchParams(window.location.search).get("library")).toBe("lib-9");

		act(() => result.current.setView({ type: "vcsp.ovf" }));
		expect(result.current).toMatchObject({ libraryId: "lib-9", type: "vcsp.ovf" });
		expect(new URLSearchParams(window.location.search).get("library")).toBe("lib-9");
		expect(new URLSearchParams(window.location.search).get("type")).toBe("vcsp.ovf");
	});

	it("omits the default sort/type from the URL to keep it clean", () => {
		const { result } = renderHook(() => useContentLibraryViewFromQuery());
		act(() => result.current.setView({ sort: "size" }));
		act(() => result.current.setView({ sort: "name" }));
		expect(new URLSearchParams(window.location.search).has("sort")).toBe(false);
	});
});
