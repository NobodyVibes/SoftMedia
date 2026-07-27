import { type MediaItem } from '../../types';
import { BookOpen } from 'lucide-react';

interface BookDetailViewProps {
    item: MediaItem;
}

export default function BookDetailView({ item }: BookDetailViewProps) {
    // Authors come from two places and the richer one wins. OpenLibrary writes every author
    // of a work into `cast` tagged with the character "Author", so co-authored books list all
    // of them; the scanner's embedded read (EPUB dc:creator / PDF Info) yields a single name
    // and lands in `director`, the shared primary-creator field. Books that were never
    // enriched still show their embedded author this way.
    const castAuthors = (item.cast ?? [])
        .filter((member) => member.characters?.some((c) => c.toLowerCase() === 'author'))
        .map((member) => member.name)
        .filter(Boolean);
    const author = castAuthors.length > 0 ? castAuthors.join(', ') : item.director;

    // Only render the fields we actually have. A grid of "Unknown" reads as a broken page,
    // and for most of these there is genuinely nothing to say — a reflowable EPUB has no page
    // count, and plenty of public-domain files carry no ISBN at all.
    const details: { label: string; value: string; mono?: boolean }[] = [
        author ? { label: castAuthors.length > 1 ? 'Authors' : 'Author', value: author } : null,
        item.studio ? { label: 'Publisher', value: item.studio } : null,
        item.isbn ? { label: 'ISBN', value: item.isbn, mono: true } : null,
        item.pageCount ? { label: 'Pages', value: item.pageCount.toLocaleString() } : null,
        item.year ? { label: 'First Published', value: String(item.year) } : null,
    ].filter((d): d is { label: string; value: string; mono?: boolean } => d !== null);

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
                {details.length > 0 && (
                    <div className="bg-white/5 rounded-xl p-6 border border-white/10">
                        <h2 className="text-xl font-bold text-white mb-4 flex items-center gap-2">
                            <BookOpen className="w-5 h-5 text-primary" />
                            Book Details
                        </h2>
                        <dl className="grid grid-cols-1 sm:grid-cols-2 gap-4 text-sm">
                            {details.map(({ label, value, mono }) => (
                                <div key={label}>
                                    <dt className="text-gray-400 mb-1">{label}</dt>
                                    <dd className={`text-white font-medium ${mono ? 'font-mono tracking-tight' : ''}`}>
                                        {value}
                                    </dd>
                                </div>
                            ))}
                        </dl>
                    </div>
                )}

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

                {/* The read action is NOT duplicated here: the detail page's primary
                    button (under the cover art) is the reader's single entry point,
                    labelled by lib/bookReadLabel. */}
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

