import { render, screen, fireEvent } from "@testing-library/react";
import { beforeEach, describe, expect, it } from "vitest";
import { ThemeProvider } from "./theme";
import { useTheme } from "./theme-context";

function Probe() {
	const { theme, toggleTheme } = useTheme();
	return (
		<button type="button" onClick={toggleTheme}>
			{theme}
		</button>
	);
}

describe("ThemeProvider", () => {
	beforeEach(() => {
		window.localStorage.clear();
		delete document.documentElement.dataset.theme;
	});

	it("defaults to dark (README: dark is primary) and stamps data-theme on the root", () => {
		render(
			<ThemeProvider>
				<Probe />
			</ThemeProvider>,
		);
		expect(screen.getByRole("button")).toHaveTextContent("dark");
		expect(document.documentElement.dataset.theme).toBe("dark");
	});

	it("toggles between dark and light, and persists the choice", () => {
		render(
			<ThemeProvider>
				<Probe />
			</ThemeProvider>,
		);
		fireEvent.click(screen.getByRole("button"));
		expect(screen.getByRole("button")).toHaveTextContent("light");
		expect(document.documentElement.dataset.theme).toBe("light");
		expect(window.localStorage.getItem("waypoint.theme")).toBe("light");
	});
});
