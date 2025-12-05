import { type MediaItem } from '../../types';
import { BookOpen } from 'lucide-react';
import { Link } from 'react-router-dom';

interface BookDetailViewProps {
    item: MediaItem;
}

export default function BookDetailView({ item }: BookDetailViewProps) {
    const metadata = item.metadata || {};

    return (
        <div className="grid grid-cols-1 md:grid-cols-3 gap-8">
            <div className="md:col-span-2 space-y-6">
                {/* Book Info */}
                <div className="bg-white/5 rounded-xl p-6 border border-white/10">
                    <h2 className="text-xl font-bold text-white mb-4 flex items-center gap-2">
                        <BookOpen className="w-5 h-5 text-primary" />
                        Book Details
                    </h2>
                    <div className="grid grid-cols-1 sm:grid-cols-2 gap-4 text-sm">
                        <div>
                            <span className="block text-gray-400 mb-1">Author</span>
                            <span className="text-white font-medium">{metadata.author || 'Unknown'}</span>
                        </div>
                        <div>
                            <span className="block text-gray-400 mb-1">Publisher</span>
                            <span className="text-white font-medium">{metadata.publisher || 'Unknown'}</span>
                        </div>
                        <div>
                            <span className="block text-gray-400 mb-1">ISBN</span>
                            <span className="text-white font-medium">{metadata.isbn || 'N/A'}</span>
                        </div>
                        <div>
                            <span className="block text-gray-400 mb-1">Pages</span>
                            <span className="text-white font-medium">{metadata.pageCount || 'Unknown'}</span>
                        </div>
                    </div>
                </div>

                {/* Read Button */}
                <Link
                    to={`/read/${item.id}`}
                    className="inline-flex items-center justify-center w-full py-3 bg-primary hover:bg-primary/90 text-white rounded-lg font-bold transition-colors"
                >
                    Read Now
                </Link>
            </div>

            {/* Sidebar / Additional Info */}
            <div className="space-y-6">
                {/* Could add similar books, or author bio here */}
            </div>
        </div>
    );
}
