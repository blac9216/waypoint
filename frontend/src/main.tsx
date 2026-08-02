import { StrictMode } from "react";
import { createRoot } from "react-dom/client";
import { registerSW } from "virtual:pwa-register";
import App from "./App.tsx";
import "./styles/global.css";

// autoUpdate: the appliance is air-gapped/long-lived — a stale cached shell
// is a worse failure mode here than a silent background update. See
// vite.config.ts's VitePWA block for the workbox strategy.
registerSW({ immediate: true });

createRoot(document.getElementById("root")!).render(
	<StrictMode>
		<App />
	</StrictMode>,
);

console.log("temp", "https://fonts.googleapis.com/test");
