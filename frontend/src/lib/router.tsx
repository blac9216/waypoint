import {
	useCallback,
	useEffect,
	useMemo,
	useState,
	type AnchorHTMLAttributes,
	type ReactNode,
} from "react";
import { routeForPath } from "./routes";
import { RouterContext, useRouter, type RouterContextValue } from "./router-context";

export function RouterProvider({ children }: { children: ReactNode }) {
	// `path` is the bare pathname ONLY (no query string) — `routes.ts`'s
	// `ROUTES` table and `routeForPath` both key on the plain path
	// (`/live-run`, never `/live-run?run=...`), and consumers that need the
	// query string (useRunIdFromQuery) read `window.location.search`
	// directly. Keeping a query string in this state was a real bug (found
	// live by issue #468's Playwright suite): `next` (e.g.
	// "/live-run?run=<id>", exactly what useScanWizard.ts's post-submit
	// navigate() call passes) was stored verbatim, `routeForPath(path)`
	// could never match any ROUTES entry against a path+query string, so
	// `route` came back `undefined` and App.tsx's `route ?? DEFAULT_ROUTE`
	// silently fell back to Dashboard on every submit — the whole
	// Start-a-Scan -> Live Run handoff redirected to Dashboard instead of
	// landing on the run.
	const [path, setPath] = useState(() => window.location.pathname);

	useEffect(() => {
		const onPopState = () => setPath(window.location.pathname);
		window.addEventListener("popstate", onPopState);
		return () => window.removeEventListener("popstate", onPopState);
	}, []);

	const navigate = useCallback((next: string) => {
		if (next !== window.location.pathname + window.location.search) {
			window.history.pushState(null, "", next);
		}
		// `next` may carry a query string (e.g. "/live-run?run=<id>") -- strip
		// it before storing so `route` matches the plain ROUTES entry.
		const nextPath = next.split("?")[0];
		setPath(nextPath);
	}, []);

	const value = useMemo<RouterContextValue>(
		() => ({ path, route: routeForPath(path), navigate }),
		[path, navigate],
	);

	return <RouterContext.Provider value={value}>{children}</RouterContext.Provider>;
}

export function Link({
	to,
	children,
	...rest
}: { to: string; children: ReactNode } & AnchorHTMLAttributes<HTMLAnchorElement>) {
	const { navigate } = useRouter();
	return (
		<a
			href={to}
			{...rest}
			onClick={(event) => {
				if (event.defaultPrevented || event.button !== 0 || event.metaKey || event.ctrlKey || event.shiftKey || event.altKey) {
					return;
				}
				event.preventDefault();
				navigate(to);
			}}
		>
			{children}
		</a>
	);
}
