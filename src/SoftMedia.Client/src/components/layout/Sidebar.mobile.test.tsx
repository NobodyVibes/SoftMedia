import { render, fireEvent, screen } from '@testing-library/react';
import { describe, it, expect, vi, beforeEach } from 'vitest';
import { MemoryRouter } from 'react-router-dom';
import Sidebar from './Sidebar';

vi.mock('../../store/authStore', () => ({
    useAuthStore: (selector: (s: { user: { username: string; role: string }; logout: () => void }) => unknown) =>
        selector({ user: { username: 'admin', role: 'Admin' }, logout: vi.fn() }),
}));

let sidebarCollapsed = false;
vi.mock('../../store/uiStore', () => ({
    useUIStore: () => ({ isSidebarCollapsed: sidebarCollapsed }),
}));

vi.mock('../../hooks/useLibrary', () => ({
    useLibraries: () => ({ data: [] }),
}));

vi.mock('react-i18next', () => ({
    useTranslation: () => ({ t: (k: string) => k }),
}));

function renderSidebar(props: { isMobileOpen?: boolean; onMobileClose?: () => void } = {}) {
    return render(
        <MemoryRouter>
            <Sidebar {...props} />
        </MemoryRouter>
    );
}

function drawer(container: HTMLElement): HTMLElement {
    return container.querySelector('[aria-label="Main navigation"]') as HTMLElement;
}

beforeEach(() => {
    vi.clearAllMocks();
    sidebarCollapsed = false;
});

/** SR-WI-040 — the sidebar doubles as an off-canvas drawer below `md`. */
describe('Sidebar mobile drawer', () => {
    it('is off-canvas below md by default (translated out, not display:none)', () => {
        const { container } = renderSidebar();
        const el = drawer(container);
        expect(el.className).toContain('max-md:-translate-x-full');
        // Desktop behavior preserved: fixed sidebar, transform reset at md+.
        expect(el.className).toContain('fixed');
        expect(el.className).toContain('md:translate-x-0');
    });

    it('slides in when isMobileOpen and moves focus into the drawer', () => {
        const { container } = renderSidebar({ isMobileOpen: true });
        const el = drawer(container);
        expect(el.className).toContain('max-md:translate-x-0');
        expect(el.className).not.toContain('max-md:-translate-x-full');
        expect(document.activeElement).toBe(el);
    });

    it('Escape closes the drawer', () => {
        const onMobileClose = vi.fn();
        renderSidebar({ isMobileOpen: true, onMobileClose });
        fireEvent.keyDown(document, { key: 'Escape' });
        expect(onMobileClose).toHaveBeenCalledTimes(1);
    });

    it('does not listen for Escape while closed', () => {
        const onMobileClose = vi.fn();
        renderSidebar({ isMobileOpen: false, onMobileClose });
        fireEvent.keyDown(document, { key: 'Escape' });
        expect(onMobileClose).not.toHaveBeenCalled();
    });

    it('returns focus to the opener element when the drawer closes', () => {
        const opener = document.createElement('button');
        document.body.appendChild(opener);
        opener.focus();

        const { container, rerender } = renderSidebar({ isMobileOpen: false });
        rerender(
            <MemoryRouter>
                <Sidebar isMobileOpen={true} />
            </MemoryRouter>
        );
        expect(document.activeElement).toBe(drawer(container));

        rerender(
            <MemoryRouter>
                <Sidebar isMobileOpen={false} />
            </MemoryRouter>
        );
        expect(document.activeElement).toBe(opener);
        opener.remove();
    });

    it('renders full labels in the mobile drawer even when desktop collapse is persisted', () => {
        // Persisted icon-only collapse is a desktop concept; below md (matchMedia
        // reports non-md) the drawer must still show the full 256px nav.
        sidebarCollapsed = true;
        vi.stubGlobal('matchMedia', vi.fn(() => ({
            matches: false,
            addEventListener: vi.fn(),
            removeEventListener: vi.fn(),
        })));
        try {
            renderSidebar({ isMobileOpen: true });
            expect(screen.getByText('Home')).toBeInTheDocument();
            expect(screen.getByRole('link', { name: 'Settings' })).toBeInTheDocument();
            expect(screen.getByText('Sign Out')).toBeInTheDocument();
        } finally {
            vi.unstubAllGlobals();
        }
    });

    it('keeps the collapsed icon-only mode at md+ (matchMedia matches)', () => {
        sidebarCollapsed = true;
        vi.stubGlobal('matchMedia', vi.fn(() => ({
            matches: true,
            addEventListener: vi.fn(),
            removeEventListener: vi.fn(),
        })));
        try {
            renderSidebar();
            expect(screen.queryByText('Home')).toBeNull();
            expect(screen.queryByText('Sign Out')).toBeNull();
        } finally {
            vi.unstubAllGlobals();
        }
    });
});
