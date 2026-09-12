/**
 * LibraryViewShell — issue #1399 AC2. Proves the shell is a generic
 * search/sort/table renderer with no baked-in content-library logic: it
 * renders whatever `items`/`columns` it is given, calls back on search/sort
 * changes without deciding anything itself, and renders the `toolbarExtra`/
 * `headerExtra` seams verbatim.
 */
import { fireEvent, render, screen } from "@testing-library/react";
import { describe, expect, it, vi } from "vitest";
import { LibraryViewShell, type LibraryViewShellColumn } from "./LibraryViewShell";

interface Row {
	id: string;
	name: string;
}

const ROWS: Row[] = [
	{ id: "1", name: "alpha" },
	{ id: "2", name: "beta" },
];

const COLUMNS: LibraryViewShellColumn<Row>[] = [{ key: "name", header: "NAME", render: (r) => r.name }];

describe("LibraryViewShell", () => {
	it("renders one row per item using the given columns", () => {
		render(
			<LibraryViewShell
				items={ROWS}
				getId={(r) => r.id}
				columns={COLUMNS}
				search=""
				onSearchChange={() => {}}
				sortOptions={[{ value: "name", label: "Name" }]}
				sortValue="name"
				onSortChange={() => {}}
			/>,
		);
		expect(screen.getByText("alpha")).toBeInTheDocument();
		expect(screen.getByText("beta")).toBeInTheDocument();
	});

	it("shows the empty message when there are no items and not loading", () => {
		render(
			<LibraryViewShell
				items={[]}
				getId={(r) => r.id}
				columns={COLUMNS}
				search=""
				onSearchChange={() => {}}
				sortOptions={[{ value: "name", label: "Name" }]}
				sortValue="name"
				onSortChange={() => {}}
				emptyMessage="nothing here"
			/>,
		);
		expect(screen.getByText("nothing here")).toBeInTheDocument();
	});

	it("does not show the empty row while loading", () => {
		render(
			<LibraryViewShell
				items={[]}
				getId={(r) => r.id}
				columns={COLUMNS}
				search=""
				onSearchChange={() => {}}
				sortOptions={[{ value: "name", label: "Name" }]}
				sortValue="name"
				onSortChange={() => {}}
				loading
				emptyMessage="nothing here"
			/>,
		);
		expect(screen.queryByText("nothing here")).not.toBeInTheDocument();
	});

	it("calls onSearchChange/onSortChange without deciding filtering/sorting itself", () => {
		const onSearchChange = vi.fn();
		const onSortChange = vi.fn();
		render(
			<LibraryViewShell
				items={ROWS}
				getId={(r) => r.id}
				columns={COLUMNS}
				search=""
				onSearchChange={onSearchChange}
				sortOptions={[
					{ value: "name", label: "Name" },
					{ value: "size", label: "Size" },
				]}
				sortValue="name"
				onSortChange={onSortChange}
				searchAriaLabel="Search rows"
				sortAriaLabel="Sort rows"
			/>,
		);
		fireEvent.change(screen.getByLabelText("Search rows"), { target: { value: "al" } });
		expect(onSearchChange).toHaveBeenCalledWith("al");
		fireEvent.change(screen.getByLabelText("Sort rows"), { target: { value: "size" } });
		expect(onSortChange).toHaveBeenCalledWith("size");
		// The shell never filtered `items` itself — both rows are still there.
		expect(screen.getByText("alpha")).toBeInTheDocument();
		expect(screen.getByText("beta")).toBeInTheDocument();
	});

	it("renders toolbarExtra and headerExtra verbatim", () => {
		render(
			<LibraryViewShell
				items={ROWS}
				getId={(r) => r.id}
				columns={COLUMNS}
				search=""
				onSearchChange={() => {}}
				sortOptions={[{ value: "name", label: "Name" }]}
				sortValue="name"
				onSortChange={() => {}}
				toolbarExtra={<div>toolbar-seam</div>}
				headerExtra={<div>header-seam</div>}
			/>,
		);
		expect(screen.getByText("toolbar-seam")).toBeInTheDocument();
		expect(screen.getByText("header-seam")).toBeInTheDocument();
	});
});
