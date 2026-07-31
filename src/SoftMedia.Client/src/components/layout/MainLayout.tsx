import { useState } from 'react';
import { Outlet, useLocation } from 'react-router-dom';
import Sidebar from './Sidebar';
import TopBar from './TopBar';
import { useUIStore } from '../../store/uiStore';
import { useAudioStore } from '../../store/audioStore';
import { cn } from '../../lib/utils';
import { useMediaHub } from '../../hooks/useMediaHub';

export default function MainLayout() {
    const { isSidebarCollapsed } = useUIStore();
    const currentTrack = useAudioStore(s => s.currentTrack);
    const location = useLocation();

    // SR-WI-040: below `md` the sidebar is an off-canvas drawer. Open state is
    // deliberately ephemeral component state (NOT the persisted uiStore) — a
    // reopened app should never boot with the drawer covering the page.
    const [isMobileNavOpen, setIsMobileNavOpen] = useState(false);

    // Navigating anywhere closes the drawer (tapping a sidebar link changes the
    // route, so this covers link clicks without wiring every <Link>). During
    // render rather than an effect: the drawer closes in the same pass as the
    // route change instead of flashing over the new page for a frame.
    const [lastPathname, setLastPathname] = useState(location.pathname);
    if (location.pathname !== lastPathname) {
        setLastPathname(location.pathname);
        setIsMobileNavOpen(false);
    }

    // App-wide SignalR connection for broadcast events (ScanProgress and friends).
    // Without this, scan status/toast updates only worked on pages that happened to
    // mount their own hub (library/detail) — the Settings page got nothing.
    useMediaHub({});

    return (
        <div className="min-h-screen bg-background text-white font-sans">
            <TopBar
                isMobileNavOpen={isMobileNavOpen}
                onOpenMobileNav={() => setIsMobileNavOpen(true)}
            />

            <div className="flex pt-16">
                {/* Backdrop for the mobile drawer — click closes. Sits below the
                    drawer (z-40) and the TopBar (z-50), above the page content. */}
                {isMobileNavOpen && (
                    <button
                        type="button"
                        // Not a tab stop — Escape (handled in Sidebar) is the
                        // keyboard path; this is the pointer dismissal surface.
                        tabIndex={-1}
                        aria-label="Close navigation menu"
                        className="fixed inset-0 z-30 bg-black/50 md:hidden cursor-default"
                        onClick={() => setIsMobileNavOpen(false)}
                        data-testid="mobile-nav-backdrop"
                    />
                )}

                <Sidebar
                    isMobileOpen={isMobileNavOpen}
                    onMobileClose={() => setIsMobileNavOpen(false)}
                />

                <main className={cn(
                    "flex-1 overflow-y-auto h-[calc(100vh-4rem)] transition-all duration-300 ease-in-out",
                    // Below md the sidebar is an overlay, so content takes the full width.
                    "ml-0",
                    isSidebarCollapsed ? "md:ml-20" : "md:ml-64",
                    currentTrack ? "pb-24" : "pb-4"
                )}>
                    <Outlet />
                </main>
            </div>
        </div>
    );
}
