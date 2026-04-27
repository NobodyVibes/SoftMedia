import { render, screen, fireEvent } from '@testing-library/react';
import { act } from '@testing-library/react';
import { describe, it, expect, vi, beforeEach } from 'vitest';
import ReaderSettingsPanel, {
    FontSizeControl,
    PanelSection,
    SegmentedControl,
} from './ReaderSettingsPanel';
import { useReaderStore } from '../../store/readerStore';

const STORAGE_KEY = 'softmedia.reader.prefs.v1';

beforeEach(() => {
    window.localStorage.removeItem(STORAGE_KEY);
    act(() => useReaderStore.getState().resetReaderPrefs());
});

describe('ReaderSettingsPanel (shell)', () => {
    it('renders the empty-state body when no children are passed', () => {
        render(
            <ReaderSettingsPanel open={true} onClose={() => {}} />,
        );

        expect(screen.getByText('Reader settings')).toBeInTheDocument();
        expect(screen.getByText(/no settings yet/i)).toBeInTheDocument();
        expect(screen.getByRole('button', { name: /close reader settings/i })).toBeInTheDocument();
        expect(screen.getByRole('button', { name: /reset to defaults/i })).toBeInTheDocument();
    });

    it('does not render any drawer chrome when closed', () => {
        render(
            <ReaderSettingsPanel open={false} onClose={() => {}} />,
        );
        expect(screen.queryByText('Reader settings')).not.toBeInTheDocument();
    });

    it('closes on Escape', () => {
        const onClose = vi.fn();
        render(
            <ReaderSettingsPanel open={true} onClose={onClose} />,
        );
        fireEvent.keyDown(window, { key: 'Escape' });
        expect(onClose).toHaveBeenCalled();
    });

    it('resetReaderPrefs button reverts the store to defaults', () => {
        // Dirty the store first.
        act(() => {
            useReaderStore.getState().setTheme('sepia');
            useReaderStore.getState().setFontSize(140);
        });

        render(
            <ReaderSettingsPanel open={true} onClose={() => {}} />,
        );
        fireEvent.click(screen.getByRole('button', { name: /reset to defaults/i }));

        const s = useReaderStore.getState();
        expect(s.theme).toBe('dark');
        expect(s.fontSize).toBe(100);
    });
});

describe('SegmentedControl', () => {
    it('renders all options and marks the selected one via aria-checked', () => {
        render(
            <SegmentedControl
                label="Theme"
                value="sepia"
                options={[
                    { value: 'dark', label: 'Dark' },
                    { value: 'sepia', label: 'Sepia' },
                    { value: 'high-contrast', label: 'High contrast' },
                ]}
                onChange={() => {}}
            />,
        );
        const sepia = screen.getByRole('radio', { name: 'Sepia' });
        expect(sepia).toHaveAttribute('aria-checked', 'true');
        const dark = screen.getByRole('radio', { name: 'Dark' });
        expect(dark).toHaveAttribute('aria-checked', 'false');
    });

    it('fires onChange with the option value', () => {
        const onChange = vi.fn();
        render(
            <SegmentedControl
                label="Theme"
                value="dark"
                options={[
                    { value: 'dark', label: 'Dark' },
                    { value: 'sepia', label: 'Sepia' },
                ]}
                onChange={onChange}
            />,
        );
        fireEvent.click(screen.getByRole('radio', { name: 'Sepia' }));
        expect(onChange).toHaveBeenCalledWith('sepia');
    });
});

describe('FontSizeControl', () => {
    it('displays the current value as percentage with aria-valuenow', () => {
        render(<FontSizeControl value={120} onChange={() => {}} />);
        const status = screen.getByRole('status');
        expect(status).toHaveTextContent('120%');
        expect(status).toHaveAttribute('aria-valuenow', '120');
    });

    it('advances by the configured step when +/- are clicked', () => {
        const onChange = vi.fn();
        render(<FontSizeControl value={100} onChange={onChange} />);
        fireEvent.click(screen.getByRole('button', { name: /increase/i }));
        expect(onChange).toHaveBeenLastCalledWith(110);
        fireEvent.click(screen.getByRole('button', { name: /decrease/i }));
        expect(onChange).toHaveBeenLastCalledWith(90);
    });

    it('disables the minus button at the minimum', () => {
        render(<FontSizeControl value={80} onChange={() => {}} />);
        expect(screen.getByRole('button', { name: /decrease/i })).toBeDisabled();
        expect(screen.getByRole('button', { name: /increase/i })).not.toBeDisabled();
    });

    it('disables the plus button at the maximum', () => {
        render(<FontSizeControl value={160} onChange={() => {}} />);
        expect(screen.getByRole('button', { name: /increase/i })).toBeDisabled();
        expect(screen.getByRole('button', { name: /decrease/i })).not.toBeDisabled();
    });
});

describe('PanelSection', () => {
    it('renders title, description, and children', () => {
        render(
            <PanelSection title="Display" description="Spread and layout">
                <div>inside</div>
            </PanelSection>,
        );
        expect(screen.getByText('Display')).toBeInTheDocument();
        expect(screen.getByText('Spread and layout')).toBeInTheDocument();
        expect(screen.getByText('inside')).toBeInTheDocument();
    });
});
