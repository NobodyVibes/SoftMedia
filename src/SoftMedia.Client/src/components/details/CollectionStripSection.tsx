import { Link } from 'react-router-dom';
import { useQuery } from '@tanstack/react-query';
import { Layers, Sparkles } from 'lucide-react';
import { collectionService, type CollectionEntry } from '../../services/collectionService';
import { attachAuthToApiUrl } from '../../lib/mediaImageUrl';
import { API_URL } from '../../services/api';

/**
 * Wave E2 — "More from this collection" strip on the movie detail view.
 *
 * Mirrors the TV episode strip pattern (TVDetailView): horizontal-scroll list
 * of sibling movies ordered by ReleaseDate ascending, with the current movie
 * marked via an "Now viewing" badge.
 *
 * Render rules (handled by the API):
 *   - 204 No Content → don't render anything (no collection, or fewer than
 *     2 visible siblings — single-movie strips are noise).
 *   - 200 → header reads "More from <em>{name}</em>" with auto-collection
 *     marker if applicable.
 *
 * No edit mode here — clicking a card navigates to that movie's detail page,
 * which itself renders its own collection strip with a different "current"
 * highlighted. That recursion is the natural Plex/Letterboxd pattern.
 */
interface CollectionStripSectionProps {
    movieId: string;
}

export default function CollectionStripSection({ movieId }: CollectionStripSectionProps) {
    const { data: collection, isLoading } = useQuery({
        queryKey: ['collection-by-movie', movieId],
        queryFn: () => collectionService.getByMovie(movieId),
        // Don't refetch on every focus — the franchise membership is stable.
        staleTime: 60_000,
    });

    if (isLoading) return null;
    if (!collection) return null; // 204 from the API → no strip

    return (
        <section aria-labelledby={`collection-${collection.id}`}>
            <div className="flex items-baseline justify-between gap-3 mb-4 flex-wrap">
                <h2
                    id={`collection-${collection.id}`}
                    className="text-xl font-bold text-white flex items-center gap-2 min-w-0"
                >
                    <Layers className="w-5 h-5 text-violet-400 shrink-0" />
                    <span>
                        More from <em className="not-italic font-bold bg-clip-text text-transparent bg-brand-gradient">{collection.name}</em>
                    </span>
                </h2>

                <div className="flex items-center gap-3 text-xs text-gray-500">
                    {collection.isAuto && (
                        <span className="inline-flex items-center gap-1.5" title="Auto-detected via Wikidata">
                            <Sparkles className="w-3 h-3" />
                            Auto
                        </span>
                    )}
                    <Link
                        to={`/collections/${collection.id}`}
                        className="text-primary hover:underline focus-visible:underline focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-blue-400 rounded px-1"
                    >
                        See all
                    </Link>
                </div>
            </div>

            {/* Horizontal scroll list — same shape as cast strips and TV episode lists */}
            <div className="flex gap-3 overflow-x-auto pb-3 -mx-1 px-1 snap-x snap-mandatory scrollbar-thin scrollbar-thumb-white/10 scrollbar-track-transparent">
                {collection.items.map(entry => (
                    <CollectionStripCard key={entry.media.id} entry={entry} />
                ))}
            </div>
        </section>
    );
}

function CollectionStripCard({ entry }: { entry: CollectionEntry }) {
    const movie = entry.media;
    const poster = resolveImageUrl(movie.posterPath);

    return (
        <Link
            to={`/media/${movie.id}`}
            className="group relative w-32 sm:w-40 shrink-0 snap-start focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-blue-400 rounded-lg"
            aria-label={`${movie.title}${entry.isCurrent ? ' (now viewing)' : ''}`}
        >
            <div className="relative aspect-[2/3] rounded-lg overflow-hidden bg-gray-800 ring-1 ring-white/10">
                {poster ? (
                    <img
                        src={poster}
                        alt=""
                        referrerPolicy="no-referrer"
                        className="w-full h-full object-cover transition-transform duration-300 group-hover:scale-105"
                    />
                ) : (
                    <div className="w-full h-full flex items-center justify-center text-gray-600 text-3xl font-bold">
                        {movie.title.charAt(0).toUpperCase()}
                    </div>
                )}

                {entry.isCurrent && (
                    <div className="absolute inset-0 ring-2 ring-primary rounded-lg pointer-events-none" />
                )}
                {entry.isCurrent && (
                    <div className="absolute top-2 left-2 bg-primary text-white text-[10px] font-bold uppercase tracking-wider px-2 py-1 rounded">
                        Now viewing
                    </div>
                )}
            </div>

            <div className="mt-2 px-0.5">
                <div className="text-sm text-white font-medium truncate">{movie.title}</div>
                {movie.year ? <div className="text-xs text-gray-500">{movie.year}</div> : null}
            </div>
        </Link>
    );
}

function resolveImageUrl(path: string | null | undefined): string | null {
    if (!path) return null;
    if (path.startsWith('/api/')) return attachAuthToApiUrl(path);
    if (path.startsWith('http')) return path;
    return `${API_URL}${path}`;
}
