import { render, screen } from "@testing-library/react";
import { describe, expect, it } from "vitest";
import { PlaceholderScreen } from "./PlaceholderScreen";

describe("PlaceholderScreen", () => {
	it("renders the title and the future-PR note", () => {
		render(<PlaceholderScreen title="Transfer" reads={[]} />);

		expect(screen.getByRole("heading", { name: "Transfer" })).toBeInTheDocument();
		expect(screen.getByText(/Screen content lands in a future PR/)).toBeInTheDocument();
	});

	it("lists each declared read when reads is non-empty", () => {
		render(<PlaceholderScreen title="Transfer" reads={["GET /api/v1/bundles", "GET /api/v1/sites"]} />);

		expect(screen.getByText("Will read from")).toBeInTheDocument();
		expect(screen.getByText("GET /api/v1/bundles")).toBeInTheDocument();
		expect(screen.getByText("GET /api/v1/sites")).toBeInTheDocument();
	});

	it("omits the reads section entirely when reads is empty", () => {
		render(<PlaceholderScreen title="Transfer" reads={[]} />);

		expect(screen.queryByText("Will read from")).not.toBeInTheDocument();
	});

	it("renders children below the note", () => {
		render(
			<PlaceholderScreen title="Transfer" reads={[]}>
				<button type="button">child action</button>
			</PlaceholderScreen>,
		);

		expect(screen.getByRole("button", { name: "child action" })).toBeInTheDocument();
	});
});
