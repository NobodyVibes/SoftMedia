import { render, screen, fireEvent } from '@testing-library/react';
import { describe, it, expect, vi, beforeEach } from 'vitest';
import { MemoryRouter } from 'react-router-dom';
import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import TopBar from './TopBar';

const mockNavigate = vi.fn();
vi.mock('react-router-dom', async () => ({
    ...(await vi.importActual('react-router-dom')),
    useNavigate: () => mockNavigate,
}));

const mockLogout = vi.fn();
vi.mock('../../store/authStore', () => ({
    useAuthStore: (selector: (s: { user: { username: string; role: string }; logout: typeof mockLogout }) => unknown) =>
        selector({ user: { username: 'admin', role: 'Admin' }, logout: mockLogout }),
}));

const mockToggleSidebar = vi.fn();
vi.mock('../../store/uiStore', () => ({
    useUIStore: () => ({ toggleSidebar: mockToggleSidebar, isSidebarCollapsed: false }),
}));

vi.mock('../../services/searchService', () => ({
    searchService: { globalSearch: vi.fn().mockResolvedValue([]) },
}));

vi.mock('../../services/notificationService', () => ({
    notificationService: {
        getNotifications: vi.fn().mockResolvedValue([
            { id: 'n1', title: 'Disk almost full', message: 'C: is at 95%', severity: 'warning' },
        ]),
        dismissNotification: vi.fn().mockResolvedValue(undefined),
    },
}));

vi.mock('../../services/libraryService', () => ({
    libraryService: { getScanQueue: vi.fn().mockResolvedValue([]) },
}));

function renderTopBar(props: { isMobileNavOpen?: boolean; onOpenMobileNav?: () => void } = {}) {
    const queryClient = new QueryClient({ defaultOptions: { queries: { retry: false } } });
    return render(
        <QueryClientProvider client={queryClient}>
            <MemoryRouter>
                <TopBar {...props} />
            </MemoryRouter>
        </QueryClientProvider>
    );
}

beforeEach(() => vi.clearAllMocks());

/** SR-WI-040 — responsive shell TopBar changes + verified TopBar bug fixes. */
describe('TopBar', () => {
    it('"View in Admin Dashboard" navigates to /settings/admin (not the dead ?tab=admin query)', async () => {
        renderTopBar();

        // Bell only renders for admins; the notification must resolve first.
        const bell = await screen.findByLabelText(/Notifications/);
        fireEvent.click(bell);

        const adminLink = await screen.findByText('View in Admin Dashboard');
        fireEvent.click(adminLink);

        expect(mockNavigate).toHaveBeenCalledWith('/settings/admin');
        expect(mockNavigate).not.toHaveBeenCalledWith('/settings?tab=admin');
    });

    it('user menu: dead items removed, "View Profile" links to /account, Sign Out remains', () => {
        renderTopBar();

        fireEvent.click(screen.getByLabelText('User menu'));

        expect(screen.queryByText('Report Issues')).toBeNull();
        expect(screen.queryByText('Help')).toBeNull();
        expect(screen.queryByText('Switch User')).toBeNull();

        const profile = screen.getByText('View Profile').closest('a');
        expect(profile).toHaveAttribute('href', '/account');
        expect(screen.getByText('Sign Out')).toBeInTheDocument();
    });

    it('hamburger is dual-role: mobile button opens the drawer, desktop button toggles collapse', () => {
        const onOpenMobileNav = vi.fn();
        renderTopBar({ onOpenMobileNav, isMobileNavOpen: false });

        const mobileHamburger = screen.getByLabelText('Open navigation menu');
        const desktopHamburger = screen.getByLabelText('Collapse sidebar');

        // CSS-swapped at the md breakpoint.
        expect(mobileHamburger.className).toContain('md:hidden');
        expect(desktopHamburger.className).toContain('hidden md:block');

        fireEvent.click(mobileHamburger);
        expect(onOpenMobileNav).toHaveBeenCalledTimes(1);
        expect(mockToggleSidebar).not.toHaveBeenCalled();

        fireEvent.click(desktopHamburger);
        expect(mockToggleSidebar).toHaveBeenCalledTimes(1);
    });

    it('mobile hamburger reflects drawer state via aria-expanded', () => {
        renderTopBar({ isMobileNavOpen: true });
        expect(screen.getByLabelText('Open navigation menu')).toHaveAttribute('aria-expanded', 'true');
    });

    it('mobile search: icon button expands the collapsed input into an overlay row and closes again', () => {
        const { container } = renderTopBar();

        // Collapsed by default below md (hidden md:block on the container).
        const searchContainer = container.querySelector('input[placeholder="Search for movies, TV shows..."]')!
            .closest('div')!.parentElement!;
        expect(searchContainer.className).toContain('hidden md:block');

        fireEvent.click(screen.getByLabelText('Open search'));
        expect(searchContainer.className).toContain('absolute inset-x-0');
        expect(searchContainer.className).not.toContain('hidden');

        fireEvent.click(screen.getByLabelText('Close search'));
        expect(searchContainer.className).toContain('hidden md:block');
    });

    it('bell and user-menu triggers expose aria-label and aria-expanded', async () => {
        renderTopBar();

        const bell = await screen.findByLabelText(/Notifications/);
        expect(bell).toHaveAttribute('aria-expanded', 'false');
        fireEvent.click(bell);
        expect(bell).toHaveAttribute('aria-expanded', 'true');

        const userMenu = screen.getByLabelText('User menu');
        expect(userMenu).toHaveAttribute('aria-expanded', 'false');
        fireEvent.click(userMenu);
        expect(userMenu).toHaveAttribute('aria-expanded', 'true');
    });
});
