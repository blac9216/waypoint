import { createContext, useContext } from "react";
import type { RouteDef } from "./routes";

export interface RouterContextValue {
	path: string;
	route: RouteDef | undefined;
	navigate: (path: string) => void;
}

export const RouterContext = createContext<RouterContextValue | null>(null);

export function useRouter(): RouterContextValue {
	const ctx = useContext(RouterContext);
	if (!ctx) {
		throw new Error("useRouter must be used within a RouterProvider");
	}
	return ctx;
}
