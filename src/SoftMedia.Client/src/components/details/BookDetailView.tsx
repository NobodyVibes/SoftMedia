import { type MediaItem } from '../../types';
import { BookOpen } from 'lucide-react';
import { Link } from 'react-router-dom';
import { useQuery } from '@tanstack/react-query';
import { getProgress } from '../../services/bookService';

interface BookDetailViewProps {
    item: MediaItem;
}

export default function BookDetailView({ item }: BookDetailViewProps) {
    const metadata = item.metadata || {};

    const { data: progress } = useQuery({
        queryKey: ['book-progress', item.id],
        queryFn: () => getProgress(item.id),
        staleTime: 30_000,
    });

    const resumePage = progress && progress.position > 0 ? Math.floor(progress.position) : 0;
    const hasEpubResume = !!progress?.bookLocation;
    // SR-WI-063: `path` left the media DTO; the server now guarantees `container`
    // carries the file extension for book-type items instead.
    const ext = (item.container ?? '').toLowerCase();
    const showResume = (ext === 'pdf' || ext === 'cbz') ? resumePage > 1 : hasEpubResume;
    const readLabel = !showResume
        ? 'Read Now'
        : ext === 'epub'
            ? 'Continue Reading'
            : `Continue from page ${resumePage}`;

    // Extract author: prefer direct "author" key (from BookScanner), fall back to cast array (from OpenLibrary)
    let author = metadata.author as string | undefined;
    if (!author && Array.isArray(metadata.cast)) {
        const authorEntry = metadata.cast.find(
            (c: { character?: string }) => c.character === 'Author'
        );
        if (authorEntry && typeof authorEntry === 'object' && 'name' in authorEntry) {
            author = (authorEntry as { name: string }).name;
        }
    }

    return (
        <div className="grid grid-cols-1 md:grid-cols-3 gap-8">
            <div className="md:col-span-2 space-y-6">
                {/* Description */}
                {item.description && (
                    <div className="bg-white/5 rounded-xl p-6 border border-white/10">
                        <p className="text-gray-300 leading-relaxed">{item.description}</p>
                    </div>
                )}

                {/* Book Info */}
                <div className="bg-white/5 rounded-xl p-6 border border-white/10">
                    <h2 className="text-xl font-bold text-white mb-4 flex items-center gap-2">
                        <BookOpen className="w-5 h-5 text-primary" />
                        Book Details
                    </h2>
                    <div className="grid grid-cols-1 sm:grid-cols-2 gap-4 text-sm">
                        <div>
                            <span className="block text-gray-400 mb-1">Author</span>
                            <span className="text-white font-medium">{author || 'Unknown'}</span>
                        </div>
                        <div>
                            <span className="block text-gray-400 mb-1">Publisher</span>
                            <span className="text-white font-medium">{(metadata.publisher as string) || (metadata.studio as string) || 'Unknown'}</span>
                        </div>
                        <div>
                            <span className="block text-gray-400 mb-1">ISBN</span>
                            <span className="text-white font-medium">{(metadata.isbn as string) || 'N/A'}</span>
                        </div>
                        <div>
                            <span className="block text-gray-400 mb-1">Pages</span>
                            <span className="text-white font-medium">{metadata.pageCount ? String(metadata.pageCount) : 'Unknown'}</span>
                        </div>
                        {item.year && (
                            <div>
                                <span className="block text-gray-400 mb-1">First Published</span>
                                <span className="text-white font-medium">{item.year}</span>
                            </div>
                        )}
                    </div>
                </div>

                {/* Genres */}
                {item.genres && item.genres.length > 0 && (
                    <div className="flex flex-wrap gap-2">
                        {item.genres.map((genre) => (
                            <span
                                key={genre}
                                className="px-3 py-1 bg-white/5 border border-white/10 rounded-full text-xs text-gray-300 font-medium"
                            >
                                {genre}
                            </span>
                        ))}
                    </div>
                )}

                {/* Read Button */}
                <Link
                    to={`/read/${item.id}`}
                    className="inline-flex items-center justify-center w-full py-3 bg-primary hover:bg-primary/90 text-white rounded-lg font-bold transition-colors focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-blue-400"
                >
                    {readLabel}
                </Link>
            </div>

            {/* Sidebar / Additional Info */}
            <div className="space-y-6 md:col-span-1">
                <div className="rounded-xl overflow-hidden shadow-2xl border border-white/10 bg-gray-900 aspect-[2/3] sticky top-8">
                    {item.posterPath ? (
                        <img 
                            src={item.posterPath} 
                            alt={item.title}
                            className="w-full h-full object-cover"
                        />
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

