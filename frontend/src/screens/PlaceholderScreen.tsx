import type { ReactNode } from "react";
import "./PlaceholderScreen.css";

/**
 * Every screen below the chrome is a placeholder — issue #11 scopes the
 * global chrome (top bar, rail, job log drawer) and login, not the nine
 * screens themselves (docs/ui/prototype/README.md "Screens"). Each of those
 * screens is its own future PR against the data ledger in
 * docs/api-contract.md; this component exists so navigation, the role/mode
 * guard, and the screen-title chrome binding all have somewhere real to
 * land and be exercised end to end today.
 */
export function PlaceholderScreen({
	title,
	reads,
	children,
}: {
	title: string;
	reads: string[];
	children?: ReactNode;
}) {
	return (
		<div className="placeholder-screen">
			<div className="placeholder-screen__card">
				<h1>{title}</h1>
				<p className="placeholder-screen__note">
					Screen content lands in a future PR (docs/ui/prototype/README.md "Screens"; data ledger in
					docs/api-contract.md). This build proves the chrome routes here, applies the role/mode guard, and
					sets the top-bar screen title correctly.
				</p>
				{reads.length > 0 && (
					<div className="placeholder-screen__reads">
						<div className="placeholder-screen__reads-label">Will read from</div>
						<ul>
							{reads.map((r) => (
								<li key={r} className="mono">
									{r}
								</li>
							))}
						</ul>
					</div>
				)}
				{children}
			</div>
		</div>
	);
}
