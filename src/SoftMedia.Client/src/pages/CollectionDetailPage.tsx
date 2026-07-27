import { useParams, Link } from 'react-router-dom';
import { useQuery } from '@tanstack/react-query';
import { Layers, Sparkles, Loader2 } from 'lucide-react';
import { BackButton } from '../components/ui/BackButton';
import { collectionService, type CollectionEntry } from '../services/collectionService';
import { resolveCardPosterUrl, resolveHeroPosterUrl } from '../lib/mediaImageUrl';
import { useMediaTokenRefresh } from '../hooks/useMediaTokenRefresh';

/**
 * Wave E2 — full collection detail page. Reachable from the "See all" link
 * in the MovieDetailView strip and from any future Collections home row.
 *
 * Layout intentionally minimal: title + description + grid of movies, no
 * mutation affordances here. Manual collections are admin-edited from a
 * settings page (out of scope for this PR; placeholder noted below).
 */
export default function CollectionDetailPage() {
    // Media URLs below embed the media token; re-render when it rotates so a
    // stale token can't leave the artwork permanently broken.
    useMediaTokenRefresh();
    const { id = '' } = useParams<{ id: string }>();
    const { data, isLoading } = useQuery({
        queryKey: ['collection', id],
        queryFn: () => collectionService.get(id),
        enabled: !!id,
    });

    if (isLoading) {
        return (
            <div className="min-h-screen flex items-center justify-center text-gray-400">
                <Loader2 className="w-6 h-6 animate-spin" />
            </div>
        );
    }
    if (!data) {
        return (
            <div className="min-h-screen flex items-center justify-center text-gray-400">
                Collection not found.
            </div>
        );
    }

    return (
        <div className="min-h-screen bg-gradient-to-br from-[#0a0a0a] via-[#121212] to-[#1a1a1a] p-6 text-white">
            <div className="max-w-6xl mx-auto">
                <BackButton to={data.items[0] ? `/media/${data.items[0].media.id}` : '/'} />

                <header className="flex flex-col sm:flex-row sm:items-end gap-6 mb-8">
                    <div className="w-32 h-48 sm:w-40 sm:h-60 rounded-xl overflow-hidden bg-brand-gradient flex items-center justify-center shrink-0 shadow-2xl">
                        {data.posterUrl ? (
                            <img
                                src={resolveHeroPosterUrl(data.posterUrl)!}
                                referrerPolicy="no-referrer"
                                alt=""
                                className="w-full h-full object-cover"
                            />
                        ) : (
                            <Layers className="w-16 h-16 text-white" />
                        )}
                    </div>
                    <div className="flex-1 min-w-0">
                        <div className="text-xs uppercase tracking-wider text-gray-500 font-bold mb-2 flex items-center gap-2">
                            Collection
                            {data.isAuto && (
                                <span className="inline-flex items-center gap-1 text-violet-400" title="Auto-detected via Wikidata">
                                    <Sparkles className="w-3 h-3" /> Auto
                                </span>
                            )}
                        </div>
                        <h1 className="text-2xl sm:text-4xl font-bold mb-3">{data.name}</h1>
                        <div className="text-sm text-gray-400">
                            {data.items.length} {data.items.length === 1 ? 'movie' : 'movies'}
                        </div>
                        {data.overview && (
                            <p className="text-gray-300 text-sm leading-relaxed mt-4 max-w-2xl">{data.overview}</p>
                        )}
                    </div>
                </header>

                <div className="grid grid-cols-2 sm:grid-cols-3 md:grid-cols-4 lg:grid-cols-5 xl:grid-cols-6 gap-4">
                    {data.items.map(entry => <CollectionMovieCard key={entry.media.id} entry={entry} />)}
                </div>
            </div>
        </div>
    );
}

function CollectionMovieCard({ entry }: { entry: CollectionEntry }) {
    // Media URLs below embed the media token; re-render when it rotates so a
    // stale token can't leave the artwork permanently broken.
    useMediaTokenRefresh();
    const movie = entry.media;
    const poster = resolveCardPosterUrl(movie.posterPath);

    return (
        <Link
            to={`/media/${movie.id}`}
            className="group focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-blue-400 rounded-lg"
        >
            <div className="relative aspect-[2/3] rounded-lg overflow-hidden bg-gray-800 ring-1 ring-white/10 transition-transform group-hover:scale-[1.02]">
                {poster ? (
                    <img
                        src={poster}
                        alt=""
                        referrerPolicy="no-referrer"
                        className="w-full h-full object-cover"
                    />
                ) : (
                    <div className="w-full h-full flex items-center justify-center text-gray-600 text-3xl font-bold">
                        {movie.title.charAt(0).toUpperCase()}
                    </div>
                )}
            </div>
            <div className="mt-2">
                <div className="text-sm text-white font-medium truncate">{movie.title}</div>
                {movie.year ? <div className="text-xs text-gray-500">{movie.year}</div> : null}
            </div>
        </Link>
    );
}
