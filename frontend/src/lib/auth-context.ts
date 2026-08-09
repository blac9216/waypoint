import { createContext, useContext } from "react";
import type { Role } from "./roles";

export interface AuthUser {
	username: string;
	role: Role;
}

export interface AuthContextValue {
	user: AuthUser | null;
	token: string | null;
	status: "restoring" | "signed-out" | "signing-in" | "signed-in";
	error: string | null;
	login: (username: string, password: string) => Promise<void>;
	logout: () => void;
}

export const AuthContext = createContext<AuthContextValue | null>(null);

export function useAuth(): AuthContextValue {
	const ctx = useContext(AuthContext);
	if (!ctx) {
		throw new Error("useAuth must be used within an AuthProvider");
	}
	return ctx;
}
