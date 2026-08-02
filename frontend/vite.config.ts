import { defineConfig } from "vite";
import react from "@vitejs/plugin-react";
import { VitePWA } from "vite-plugin-pwa";
import { mockBackendPlugin } from "./vite-plugins/mock-backend.ts";

// https://vite.dev/config/
export default defineConfig({
	plugins: [
		react(),
		// Dev-only local-auth/system/events mock — see vite-plugins/mock-backend.ts.
		// `apply: 'serve'` inside the plugin already keeps it out of `vite build`;
		// this is defense in depth.
		mockBackendPlugin(),
		VitePWA({
			// PWA shell for an air-gapped operator (ADR-0007): installable,
			// works offline once the shell is cached. generateSW (workbox) — no
			// external CDN in the generated service worker; everything precached
			// comes from this build's own dist/ output.
			registerType: "autoUpdate",
			injectRegister: false, // main.tsx calls registerSW() from virtual:pwa-register itself.
			manifest: {
				name: "Waypoint — DoD VCF Toolkit",
				short_name: "Waypoint",
				description: "STIG compliance and offline VCF artifact management console.",
				start_url: "/",
				scope: "/",
				display: "standalone",
				background_color: "#141821",
				theme_color: "#141821",
				icons: [
					{ src: "/icons/icon-192.png", sizes: "192x192", type: "image/png" },
					{ src: "/icons/icon-512.png", sizes: "512x512", type: "image/png" },
					{ src: "/icons/icon-maskable-192.png", sizes: "192x192", type: "image/png", purpose: "maskable" },
					{ src: "/icons/icon-maskable-512.png", sizes: "512x512", type: "image/png", purpose: "maskable" },
				],
			},
			workbox: {
				// SSE and REST calls must never be served from the offline cache —
				// only the static app shell is precached.
				navigateFallbackDenylist: [/^\/api\//],
				runtimeCaching: [],
			},
		}),
	],
	build: {
		// No source maps in the shipped bundle: an air-gapped appliance has no
		// business publishing its own source layout, and it keeps the
		// external-asset scan's file set unambiguous (scripts/check-no-external-assets.mjs).
		sourcemap: false,
	},
});
