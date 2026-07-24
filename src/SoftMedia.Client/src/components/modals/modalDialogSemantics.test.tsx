import { render, screen } from '@testing-library/react';
import { describe, it, expect, vi, beforeEach } from 'vitest';
import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import { ConfirmationModal } from './ConfirmationModal';
import { StreamingModal } from './StreamingModal';
import { LibraryAccessModal } from './LibraryAccessModal';
import { RatingsModal } from './RatingsModal';
import { CreateUserModal } from '../admin/CreateUserModal';
import { ResetPasswordModal } from '../admin/ResetPasswordModal';
import { userService, type UserDto } from '../../services/userService';
import { libraryService } from '../../services/libraryService';

/**
 * SR-WI-051 — every one of the six migrated modals must expose real dialog
 * semantics at runtime: role="dialog", aria-modal="true", and an accessible
 * name derived from its visible title. The static guard in
 * src/test/a11yGuards.test.ts pins that they render through the shared
 * <Modal> primitive; this file pins what that actually produces in the DOM.
 */

vi.mock('../../services/userService', () => ({
    userService: {
        createUser: vi.fn(),
        updateUserRatings: vi.fn(),
        updateUserStreaming: vi.fn(),
        resetUserPassword: vi.fn(),
        getUserLibraryAccess: vi.fn(),
        setUserLibraryAccess: vi.fn(),
    },
}));

vi.mock('../../services/libraryService', () => ({
    libraryService: { getAll: vi.fn() },
}));

vi.mock('sonner', () => ({
    toast: { success: vi.fn(), error: vi.fn() },
}));

const mockedUserService = vi.mocked(userService);
const mockedLibraryService = vi.mocked(libraryService);

const userRow: UserDto = {
    id: 'user-1',
    username: 'alice',
    role: 'User',
    maxRating: '',
    createdAt: '2026-01-01',
    isBanned: false,
    isApproved: true,
    isRejected: false,
    contentRatings: {},
    firstName: 'A',
    lastName: 'L',
    createdByAdmin: false,
    usedInviteCode: null,
    twoFactorEnabled: false,
    maxStreamBitrateKbps: 0,
};

function renderWithQuery(ui: React.ReactElement) {
    const queryClient = new QueryClient({
        defaultOptions: { queries: { retry: false }, mutations: { retry: false } },
    });
    return render(<QueryClientProvider client={queryClient}>{ui}</QueryClientProvider>);
}

function expectDialog(name: RegExp) {
    const dialog = screen.getByRole('dialog', { name });
    expect(dialog).toHaveAttribute('aria-modal', 'true');
    expect(dialog.getAttribute('aria-labelledby')).toBeTruthy();
    return dialog;
}

beforeEach(() => {
    vi.clearAllMocks();
    mockedLibraryService.getAll.mockResolvedValue([]);
    mockedUserService.getUserLibraryAccess.mockResolvedValue([]);
});

describe('modal dialog semantics (SR-WI-051)', () => {
    it('ConfirmationModal is a labelled dialog', () => {
        render(
            <ConfirmationModal
                isOpen={true}
                title="Delete library?"
                message="This cannot be undone."
                onConfirm={() => {}}
                onCancel={() => {}}
            />,
        );
        expectDialog(/delete library\?/i);
    });

    it('StreamingModal is a labelled dialog', () => {
        renderWithQuery(<StreamingModal isOpen={true} onClose={() => {}} user={userRow} />);
        expectDialog(/streaming limit for alice/i);
    });

    it('LibraryAccessModal is a labelled dialog', async () => {
        renderWithQuery(<LibraryAccessModal isOpen={true} onClose={() => {}} user={userRow} />);
        expectDialog(/library access for alice/i);
        // Let the library queries settle so nothing resolves after teardown.
        await screen.findByText(/no libraries configured/i);
    });

    it('RatingsModal is a labelled dialog', () => {
        renderWithQuery(<RatingsModal isOpen={true} onClose={() => {}} user={userRow} />);
        expectDialog(/edit content ratings for alice/i);
    });

    it('CreateUserModal is a labelled dialog', () => {
        renderWithQuery(<CreateUserModal isOpen={true} onClose={() => {}} />);
        expectDialog(/create new user/i);
    });

    it('ResetPasswordModal is a labelled dialog', () => {
        renderWithQuery(<ResetPasswordModal isOpen={true} onClose={() => {}} user={userRow} />);
        expectDialog(/reset password for alice/i);
    });
});
