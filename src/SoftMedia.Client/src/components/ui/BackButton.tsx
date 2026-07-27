import { Link } from 'react-router-dom';
import { ArrowLeft } from 'lucide-react';
import { cn } from '../../lib/utils';

interface BackButtonProps {
    /**
     * Hierarchical destination, resolved through lib/backNavigation (never
     * browser history — see that module for why).
     */
    to: string;
    /** Extra layout classes for the call site; spacing defaults to `mb-8`. */
    className?: string;
}

/**
 * The app's one back control.
 *
 * Every detail surface used to draw its own: media detail pages had a circular
 * icon chip reading "Back", while playlists and collections had a small grey
 * text link with a bespoke label. Same gesture, three appearances. This is the
 * media-detail treatment — the one the majority of pages already showed —
 * promoted to a shared component so the next page can't invent a fourth.
 *
 * Renders a real `<Link>` rather than a button that calls `navigate()`: the
 * destination is always a plain path, and a link gets keyboard, middle-click
 * and open-in-new-tab behaviour for free.
 *
 * The label is deliberately fixed. A back control that says something different
 * on every page is the thing this component exists to prevent; pages that need
 * to name a specific destination should do it in their heading, not here.
 */
export function BackButton({ to, className }: BackButtonProps) {
    return (
        <Link
            to={to}
            className={cn(
                'inline-flex items-center gap-2 text-gray-300 hover:text-white transition-colors group',
                'focus-visible:outline-none focus-visible:text-white rounded mb-8',
                className
            )}
        >
            <div className="p-2 rounded-full bg-black/20 group-hover:bg-black/40 group-focus-visible:ring-2 group-focus-visible:ring-blue-400 transition-colors">
                <ArrowLeft className="w-5 h-5" />
            </div>
            <span className="font-medium">Back</span>
        </Link>
    );
}

export default BackButton;
