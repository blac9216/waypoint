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
	const [path, setPath] = useState(() => window.location.pathname);

	useEffect(() => {
		const onPopState = () => setPath(window.location.pathname);
		window.addEventListener("popstate", onPopState);
		return () => window.removeEventListener("popstate", onPopState);
	}, []);

	const navigate = useCallback((next: string) => {
		if (next !== window.location.pathname) {
			window.history.pushState(null, "", next);
		}
		setPath(next);
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
