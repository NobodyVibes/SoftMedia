import { useState } from 'react';
import { useMutation, useQueryClient } from '@tanstack/react-query';
import { Bookmark, Check, Loader2 } from 'lucide-react';
import { toast } from 'sonner';
import { watchlistService } from '../../services/watchlistService';
import { cn } from '../../lib/utils';

interface WatchlistButtonProps {
    mediaId: string;
    /** Whether the item is currently in the user's watchlist (server-truth). */
    isWatchlisted: boolean;
    /** Optional title for the toast message; defaults to a generic word. */
    title?: string;
    /** Layout variant — affects size and label visibility. */
    variant?: 'detail' | 'compact';
}

/**
 * Wave E3 — toggle a media item's presence in the user's watchlist.
 *
 * Research-driven choices:
 *   - One button, two states. No "priority" or "categories" — research
 *     finding from Letterboxd / Trakt: minimum-viable watchlist works for
 *     90% of users; flat list with date ordering is enough.
 *   - Optimistic UI: state flips immediately, server call runs in the
 *     background, revert + toast on error.
 *   - aria-pressed on the button so assistive tech announces the toggle.
 *   - 44×44 minimum hit target per the universal-client rule.
 */
export function WatchlistButton({ mediaId, isWatchlisted, title, variant = 'detail' }: WatchlistButtonProps) {
    const queryClient = useQueryClient();
    const [optimistic, setOptimistic] = useState(isWatchlisted);

    const mutation = useMutation({
        mutationFn: (next: boolean) => watchlistService.toggle(mediaId, next),
        onMutate: (next) => {
            // Optimistic flip — keeps the click feeling instant.
            const previous = optimistic;
            setOptimistic(next);
            return { previous };
        },
        onError: (err: unknown, _vars, ctx) => {
            // Revert and tell the user.
            setOptimistic(ctx?.previous ?? isWatchlisted);
            toast.error(err instanceof Error ? err.message : 'Could not update watchlist');
        },
        onSuccess: (_, next) => {
            // Cache invalidations — the home-page row / watchlist page, and the
            // detail query this page renders from. refetchType 'all' on the list:
            // toggling changes row MEMBERSHIP, and the (inactive) home query must
            // refetch now rather than serve stale membership on navigate-back
            // (same reasoning as continueWatching in MediaDetailLayout).
            queryClient.invalidateQueries({ queryKey: ['watchlist'], refetchType: 'all' });
            // The detail page reads ['media', id] (MediaDetailPage). The old key
            // here ('media-detail') matched NOTHING, so item.isWatchlisted stayed
            // stale and the sync-back below visually reverted a successful toggle.
            queryClient.invalidateQueries({ queryKey: ['media', mediaId] });
            const verb = next ? 'Added to watchlist' : 'Removed from watchlist';
            toast.success(title ? `${verb}: ${title}` : verb);
        },
    });

    // Sync local optimistic state with server-truth when the prop changes
    // (e.g. when the parent re-renders with a fresh GET result).
    if (!mutation.isPending && optimistic !== isWatchlisted) {
        setOptimistic(isWatchlisted);
    }

    const isCompact = variant === 'compact';
    const label = optimistic ? 'In watchlist' : 'Add to watchlist';

    return (
        <button
            type="button"
            aria-pressed={optimistic}
            aria-label={label}
            onClick={() => mutation.mutate(!optimistic)}
            disabled={mutation.isPending}
            className={cn(
                'inline-flex items-center gap-2 rounded-lg transition-colors min-h-[44px] focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-blue-400',
                isCompact ? 'px-3 py-2 text-sm' : 'px-4 py-3 text-sm',
                optimistic
                    ? 'bg-primary/20 text-primary hover:bg-primary/30 ring-1 ring-primary/40'
                    : 'bg-white/5 text-white hover:bg-white/10',
                mutation.isPending && 'opacity-70 cursor-wait',
            )}
        >
            {mutation.isPending ? (
                <Loader2 className="w-4 h-4 animate-spin" aria-hidden="true" />
            ) : optimistic ? (
                <Check className="w-4 h-4" aria-hidden="true" />
            ) : (
                <Bookmark className="w-4 h-4" aria-hidden="true" />
            )}
            {!isCompact && <span>{label}</span>}
        </button>
    );
}
