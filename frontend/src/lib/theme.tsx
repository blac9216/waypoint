import { createContext, useCallback, useContext, useEffect, useMemo, useState, type ReactNode } from "react";

export type Theme = "dark" | "light";

const STORAGE_KEY = "waypoint.theme";

function readStoredTheme(): Theme | null {
	try {
		const stored = window.localStorage.getItem(STORAGE_KEY);
		return stored === "dark" || stored === "light" ? stored : null;
	} catch {
		// localStorage can throw in locked-down/private-browsing contexts.
		return null;
	}
}

/** Dark is primary (docs/ui/prototype/README.md): default to dark unless the
 * operator's OS explicitly prefers light and nothing has been chosen yet. */
function initialTheme(): Theme {
	const stored = readStoredTheme();
	if (stored) {
		return stored;
	}
	if (window.matchMedia?.("(prefers-color-scheme: light)").matches) {
		return "light";
	}
	return "dark";
}

interface ThemeContextValue {
	theme: Theme;
	setTheme: (theme: Theme) => void;
	toggleTheme: () => void;
}

const ThemeContext = createContext<ThemeContextValue | null>(null);

export function ThemeProvider({ children }: { children: ReactNode }) {
	const [theme, setThemeState] = useState<Theme>(initialTheme);

	useEffect(() => {
		document.documentElement.dataset.theme = theme;
		try {
			window.localStorage.setItem(STORAGE_KEY, theme);
		} catch {
			// Best-effort persistence only.
		}
	}, [theme]);

	const setTheme = useCallback((next: Theme) => setThemeState(next), []);
	const toggleTheme = useCallback(() => setThemeState((prev) => (prev === "dark" ? "light" : "dark")), []);

	const value = useMemo(() => ({ theme, setTheme, toggleTheme }), [theme, setTheme, toggleTheme]);

	return <ThemeContext.Provider value={value}>{children}</ThemeContext.Provider>;
}

export function useTheme(): ThemeContextValue {
	const ctx = useContext(ThemeContext);
	if (!ctx) {
		throw new Error("useTheme must be used within a ThemeProvider");
	}
	return ctx;
}
