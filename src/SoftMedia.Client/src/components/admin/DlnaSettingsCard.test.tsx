import { render, screen, fireEvent, waitFor } from '@testing-library/react';
import { describe, it, expect, vi, beforeEach } from 'vitest';
import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import { DlnaSettingsCard } from './DlnaSettingsCard';
import { settingsService, type AppSetting } from '../../services/settingsService';
import { libraryService } from '../../services/libraryService';
import type { Library } from '../../types';

vi.mock('../../services/settingsService', () => ({
    settingsService: { getAll: vi.fn(), update: vi.fn() },
}));
vi.mock('../../services/libraryService', () => ({
    libraryService: { getAll: vi.fn() },
}));
vi.mock('sonner', () => ({ toast: { success: vi.fn(), error: vi.fn() } }));

const mockedSettings = vi.mocked(settingsService);
const mockedLibraries = vi.mocked(libraryService);

// A distinct server name (not the default 'SoftMedia') so tests can await it to know the settings
// query resolved and the form initialised before interacting.
const dlnaSettings: AppSetting[] = [
    { key: 'EnableDlna', value: 'false', group: 'DLNA' },
    { key: 'DlnaServerName', value: 'MyDlna', group: 'DLNA' },
    { key: 'DlnaExposedLibraries', value: '', group: 'DLNA' },
    { key: 'DlnaMaxContentRatings', value: '', group: 'DLNA' },
];

const libs: Library[] = [
    { id: 'lib-movies', name: 'Movies', type: 'Movie', paths: ['/m'], order: 0 },
    { id: 'lib-tv', name: 'Shows', type: 'TV', paths: ['/t'], order: 1 },
    { id: 'lib-books', name: 'Books', type: 'Book', paths: ['/b'], order: 2 },
];

function renderCard() {
    const qc = new QueryClient({ defaultOptions: { queries: { retry: false }, mutations: { retry: false } } });
    return render(<QueryClientProvider client={qc}><DlnaSettingsCard /></QueryClientProvider>);
}

beforeEach(() => {
    vi.clearAllMocks();
    mockedSettings.getAll.mockResolvedValue(dlnaSettings);
    mockedSettings.update.mockResolvedValue();
    mockedLibraries.getAll.mockResolvedValue(libs);
});

describe('DlnaSettingsCard', () => {
    it('lists only audio/video libraries (not Book)', async () => {
        renderCard();
        expect(await screen.findByText('Movies')).toBeInTheDocument();
        expect(screen.getByText('Shows')).toBeInTheDocument();
        expect(screen.queryByText('Books')).not.toBeInTheDocument();
    });

    it('saves enable + exposed library CSV + per-type rating JSON', async () => {
        renderCard();
        await screen.findByDisplayValue('MyDlna'); // wait until settings loaded + form initialised

        // Enable DLNA
        fireEvent.click(screen.getByRole('switch', { name: /enable dlna/i }));
        // Expose the Movies library
        fireEvent.click(screen.getByRole('checkbox', { name: /movies/i }));
        // Cap movies at PG-13
        fireEvent.change(screen.getByLabelText(/max movie rating/i), { target: { value: 'PG-13' } });

        fireEvent.click(screen.getByRole('button', { name: /save dlna settings/i }));

        await waitFor(() => expect(mockedSettings.update).toHaveBeenCalledTimes(1));
        const saved: AppSetting[] = mockedSettings.update.mock.calls[0][0];
        const byKey = Object.fromEntries(saved.map(s => [s.key, s.value]));
        expect(byKey['EnableDlna']).toBe('true');
        expect(byKey['DlnaExposedLibraries']).toBe('lib-movies');
        expect(byKey['DlnaMaxContentRatings']).toBe('{"Movie":"PG-13"}');
    });

    it('stores empty ratings JSON when nothing is capped', async () => {
        renderCard();
        await screen.findByDisplayValue('MyDlna'); // wait until settings loaded + form initialised
        fireEvent.click(screen.getByRole('button', { name: /save dlna settings/i }));

        await waitFor(() => expect(mockedSettings.update).toHaveBeenCalledTimes(1));
        const saved: AppSetting[] = mockedSettings.update.mock.calls[0][0];
        const ratings = saved.find(s => s.key === 'DlnaMaxContentRatings')!;
        expect(ratings.value).toBe('');
    });
});
