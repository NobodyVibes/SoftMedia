import { useMemo } from 'react';
import { useQuery } from '@tanstack/react-query';
import { Link } from 'react-router-dom';
import { BookOpen, Play, Star } from 'lucide-react';
import api from '../../services/api';
import { type MediaItem } from '../../types';
import { attachAuthToApiUrl } from '../../lib/mediaImageUrl';
import { useMediaTokenRefresh } from '../../hooks/useMediaTokenRefresh';
import LoadingImage from '../ui/LoadingImage';

interface ComicSeriesDetailViewProps {
    item: MediaItem;
}

export default function ComicSeriesDetailView({ item }: ComicSeriesDetailViewProps) {
    // The cover URL embeds the media token (AA-WI-001) — re-render on rotation.
    useMediaTokenRefresh();
    const { data: issues, isLoading } = useQuery({
        queryKey: ['comic-series', item.id, 'issues'],
        queryFn: async () => {
            const res = await api.get<MediaItem[]>(`/libraries/comics/${item.id}/issues`);
            return res.data;
        },
        staleTime: 60_000,
    });

    const firstIssue = issues?.[0];
    const issueCount = issues?.length ?? 0;

    // Year span from min/max issue year, falls back to the series year.
    const yearSummary = useMemo(() => {
        if (!issues || issues.length === 0) return item.year ? `${item.year}` : null;
        const years = issues.map(i => i.year).filter((y): y is number => typeof y === 'number');
        if (years.length === 0) return item.year ? `${item.year}` : null;
        const min = Math.min(...years);
        const max = Math.max(...years);
        return min === max ? `${min}` : `${min}–${max}`;
    }, [issues, item.year]);

    return (
        <div className="grid grid-cols-1 md:grid-cols-3 gap-8">
            <div className="md:col-span-2 space-y-6">
                {item.description && (
                    <div className="bg-white/5 rounded-xl p-6 border border-white/10">
                        <p className="text-gray-300 leading-relaxed">{item.description}</p>
                    </div>
                )}

                <div className="bg-white/5 rounded-xl p-6 border border-white/10">
                    <div className="flex items-center justify-between mb-4">
                        <h2 className="text-xl font-bold text-white flex items-center gap-2">
                            <BookOpen className="w-5 h-5 text-primary" />
                            Issues
                        </h2>
                        <span className="text-sm text-gray-400">
                            {issueCount} {issueCount === 1 ? 'issue' : 'issues'}
                            {yearSummary ? ` · ${yearSummary}` : ''}
                        </span>
                    </div>

                    {isLoading ? (
                        <IssueListSkeleton />
                    ) : issueCount === 0 ? (
                        <p className="text-gray-400 text-sm">No issues found in this series.</p>
                    ) : (
                        <ul className="divide-y divide-white/5">
                            {issues!.map(issue => (
                                <IssueRow key={issue.id} issue={issue} />
                            ))}
                        </ul>
                    )}
                </div>

                {item.genres && item.genres.length > 0 && (
                    <div className="flex flex-wrap gap-2">
                        {item.genres.map(g => (
                            <span
                                key={g}
                                className="px-3 py-1 bg-white/5 border border-white/10 rounded-full text-xs text-gray-300 font-medium"
                            >
                                {g}
                            </span>
                        ))}
                    </div>
                )}

                {firstIssue && (
                    <Link
                        to={`/read/${firstIssue.id}`}
                        className="inline-flex items-center justify-center w-full py-3 bg-primary hover:bg-primary/90 text-white rounded-lg font-bold transition-colors focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-blue-400"
                    >
                        <Play className="w-4 h-4 mr-2 fill-current" />
                        Start reading — Issue #{firstIssue.episodeNumber ?? 1}
                    </Link>
                )}
            </div>

            <div className="space-y-6 md:col-span-1">
                <div className="rounded-xl overflow-hidden shadow-2xl border border-white/10 bg-gray-900 aspect-[2/3] sticky top-8">
                    {item.posterPath ? (
                        <img src={attachAuthToApiUrl(item.posterPath)} alt={item.title} className="w-full h-full object-cover" />
                    ) : (
                        <div className="w-full h-full flex flex-col items-center justify-center text-gray-500">
                            <BookOpen className="w-16 h-16 mb-4 opacity-50" />
                            <span className="text-sm font-medium uppercase tracking-wider">No Cover</span>
                        </div>
                    )}
                </div>
            </div>
        </div>
    );
}

function IssueRow({ issue }: { issue: MediaItem }) {
    const displayNumber = issue.episodeNumber != null ? `#${issue.episodeNumber}` : '';
    const displayTitle = issue.title || 'Untitled';

    return (
        <li>
            <Link
                to={`/read/${issue.id}`}
                className="group flex items-center gap-4 py-3 px-2 -mx-2 rounded-md hover:bg-white/5 transition-colors focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-blue-400"
            >
                <div className="w-16 h-24 flex-shrink-0 bg-gray-800 rounded overflow-hidden">
                    {issue.posterPath ? (
                        <LoadingImage
                            src={issue.posterPath}
                            alt={displayTitle}
                            className="w-full h-full object-cover"
                        />
                    ) : (
                        <div className="w-full h-full flex items-center justify-center text-gray-600">
                            <BookOpen className="w-6 h-6" />
                        </div>
                    )}
                </div>

                <div className="flex-1 min-w-0">
                    <div className="flex items-baseline gap-2">
                        {displayNumber && (
                            <span className="font-mono text-primary font-semibold text-sm">{displayNumber}</span>
                        )}
                        <span className="text-white font-medium truncate">{displayTitle}</span>
                    </div>
                    {(issue.year || issue.userRating) && (
                        <div className="flex items-center gap-3 mt-1 text-xs text-gray-400">
                            {issue.year && <span>{issue.year}</span>}
                            {issue.userRating != null && (
                                <span className="flex items-center gap-1">
                                    <Star className="w-3 h-3 text-yellow-400 fill-current" />
                                    {issue.userRating.toFixed(1)}
                                </span>
                            )}
                        </div>
                    )}
                </div>

                <div className="opacity-0 group-hover:opacity-100 group-focus-visible:opacity-100 transition-opacity">
                    <div className="w-10 h-10 rounded-full bg-primary flex items-center justify-center">
                        <Play className="w-4 h-4 text-white fill-current" />
                    </div>
                </div>
            </Link>
        </li>
    );
}

function IssueListSkeleton() {
    return (
        <ul className="divide-y divide-white/5">
            {Array.from({ length: 3 }).map((_, i) => (
                <li key={i} className="flex items-center gap-4 py-3 px-2">
                    <div className="w-16 h-24 bg-white/5 rounded animate-pulse" />
                    <div className="flex-1 space-y-2">
                        <div className="h-4 w-1/3 bg-white/5 rounded animate-pulse" />
                        <div className="h-3 w-1/4 bg-white/5 rounded animate-pulse" />
                    </div>
                </li>
            ))}
        </ul>
    );
}
