/**
 * Hand-drawn inline SVG glyphs — lifted geometry from
 * docs/ui/prototype/vcf-ops-console.dc.html's nav icons (simple rects,
 * lines, circles, polylines; 1.4px stroke, currentColor, no fill). This is
 * the entire icon set: no icon font, no icon package, nothing fetched.
 */
import type { SVGProps } from "react";

function Icon({ children, ...props }: SVGProps<SVGSVGElement>) {
	return (
		<svg
			width="15"
			height="15"
			viewBox="0 0 16 16"
			fill="none"
			stroke="currentColor"
			strokeWidth="1.4"
			aria-hidden="true"
			{...props}
		>
			{children}
		</svg>
	);
}

export function DashboardIcon(props: SVGProps<SVGSVGElement>) {
	return (
		<Icon {...props}>
			<rect x="1.5" y="1.5" width="5" height="5" />
			<rect x="9.5" y="1.5" width="5" height="5" />
			<rect x="1.5" y="9.5" width="5" height="5" />
			<rect x="9.5" y="9.5" width="5" height="5" />
		</Icon>
	);
}

export function LiveRunIcon(props: SVGProps<SVGSVGElement>) {
	return (
		<Icon {...props}>
			<polyline points="0.5,8.5 3.5,8.5 5,3.5 7.5,12.5 10,6.5 11.5,8.5 15.5,8.5" />
		</Icon>
	);
}

export function ScanIcon(props: SVGProps<SVGSVGElement>) {
	return (
		<Icon {...props}>
			<circle cx="7" cy="7" r="5" />
			<line x1="10.7" y1="10.7" x2="15" y2="15" />
		</Icon>
	);
}

export function ResultsIcon(props: SVGProps<SVGSVGElement>) {
	return (
		<Icon {...props}>
			<line x1="2" y1="3.5" x2="14" y2="3.5" />
			<line x1="2" y1="8" x2="14" y2="8" />
			<line x1="2" y1="12.5" x2="9" y2="12.5" />
		</Icon>
	);
}

export function BenchmarksIcon(props: SVGProps<SVGSVGElement>) {
	return (
		<Icon {...props}>
			<rect x="2.5" y="1.5" width="11" height="13" />
			<line x1="5" y1="5" x2="11" y2="5" />
			<line x1="5" y1="8" x2="11" y2="8" />
			<line x1="5" y1="11" x2="9" y2="11" />
		</Icon>
	);
}

export function CatalogIcon(props: SVGProps<SVGSVGElement>) {
	return (
		<Icon {...props}>
			<line x1="8" y1="1.5" x2="8" y2="10.5" />
			<polyline points="4.5,7 8,10.5 11.5,7" />
			<line x1="2" y1="14" x2="14" y2="14" />
		</Icon>
	);
}

export function LibraryIcon(props: SVGProps<SVGSVGElement>) {
	return (
		<Icon {...props}>
			<rect x="2" y="2.5" width="3" height="11" />
			<rect x="6.5" y="2.5" width="3" height="11" />
			<line x1="11.5" y1="3.5" x2="14" y2="13" />
		</Icon>
	);
}

export function TransferIcon(props: SVGProps<SVGSVGElement>) {
	return (
		<Icon {...props}>
			<polyline points="10,2 13.5,5 10,8" />
			<line x1="13" y1="5" x2="2.5" y2="5" />
			<polyline points="6,8 2.5,11 6,14" />
			<line x1="3" y1="11" x2="13.5" y2="11" />
		</Icon>
	);
}

export function ConfigurationIcon(props: SVGProps<SVGSVGElement>) {
	return (
		<Icon {...props}>
			<circle cx="8" cy="8" r="2.4" />
			<circle cx="8" cy="8" r="6" />
			<line x1="8" y1="0.5" x2="8" y2="2" />
			<line x1="8" y1="14" x2="8" y2="15.5" />
		</Icon>
	);
}

export function ChevronIcon(props: SVGProps<SVGSVGElement>) {
	return (
		<svg width="13" height="13" viewBox="0 0 16 16" fill="none" stroke="currentColor" strokeWidth="1.5" aria-hidden="true" {...props}>
			<polyline points="10,2.5 4,8 10,13.5" />
		</svg>
	);
}

export function DrawerChevronIcon(props: SVGProps<SVGSVGElement>) {
	return (
		<svg width="11" height="11" viewBox="0 0 16 16" fill="none" stroke="currentColor" strokeWidth="1.6" aria-hidden="true" {...props}>
			<polyline points="3,10 8,5 13,10" />
		</svg>
	);
}

export function ThemeIcon() {
	return (
		<span
			aria-hidden="true"
			style={{
				width: 11,
				height: 11,
				borderRadius: "50%",
				border: "1.5px solid currentColor",
				background: "linear-gradient(90deg, currentColor 50%, transparent 50%)",
				display: "inline-block",
			}}
		/>
	);
}

export function BrandMark({ size = 20 }: { size?: number }) {
	const inset = Math.round(size * 0.2);
	return (
		<div
			aria-hidden="true"
			style={{
				width: size,
				height: size,
				border: "1.5px solid var(--acc)",
				transform: "rotate(45deg)",
				position: "relative",
				flex: "0 0 auto",
			}}
		>
			<div style={{ position: "absolute", inset, background: "var(--acc)" }} />
		</div>
	);
}
