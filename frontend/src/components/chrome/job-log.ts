const MIN_HEIGHT_PX = 96;
const MAX_HEIGHT_VH = 0.4;

/** Clamp a candidate drawer height to [96px, 40% of the current viewport
 * height] — README "Layout Rules Learned the Hard Way" #6 / "Global job log
 * drawer": "Drag-resize clamps to [96px, 40% of window height]. Without
 * those caps the drawer starves the screen above it." Exported so the clamp
 * logic itself is unit-testable without mounting the drawer. */
export function clampDrawerHeight(candidatePx: number, viewportHeightPx: number): number {
	const max = Math.round(viewportHeightPx * MAX_HEIGHT_VH);
	return Math.max(MIN_HEIGHT_PX, Math.min(max, candidatePx));
}
