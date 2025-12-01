import { useState } from 'react';
import { Document, Page, pdfjs } from 'react-pdf';
import { ReactReader } from 'react-reader';
import { ChevronLeft, ChevronRight, X } from 'lucide-react';
import { useNavigate } from 'react-router-dom';
import { API_URL } from '../../services/api';
import type { MediaItem } from '../../types';

// Setup PDF worker
pdfjs.GlobalWorkerOptions.workerSrc = new URL(
    'pdfjs-dist/build/pdf.worker.min.mjs',
    import.meta.url,
).toString();

interface BookReaderProps {
    item: MediaItem;
}

export default function BookReader({ item }: BookReaderProps) {
    const navigate = useNavigate();
    const [numPages, setNumPages] = useState<number>(0);
    const [pageNumber, setPageNumber] = useState<number>(1);
    const [location, setLocation] = useState<string | number>(0);

    const fileUrl = `${API_URL}/stream/${item.id}`;
    const isPdf = item.path?.toLowerCase().endsWith('.pdf');
    const isEpub = item.path?.toLowerCase().endsWith('.epub');

    // PDF Handlers
    function onDocumentLoadSuccess({ numPages }: { numPages: number }) {
        setNumPages(numPages);
    }

    const changePage = (offset: number) => {
        setPageNumber(prev => Math.min(Math.max(1, prev + offset), numPages));
    };

    // EPUB Handlers
    const locationChanged = (epubcifi: string | number) => {
        setLocation(epubcifi);
        // TODO: Save progress
    };

    return (
        <div className="fixed inset-0 bg-gray-900 z-50 flex flex-col">
            {/* Header */}
            <div className="h-14 bg-gray-800 flex items-center justify-between px-4 shadow-md z-10">
                <h2 className="text-white font-medium truncate">{item.title}</h2>
                <button
                    onClick={() => navigate(-1)}
                    className="p-2 hover:bg-gray-700 rounded-full text-white transition"
                >
                    <X size={24} />
                </button>
            </div>

            {/* Content */}
            <div className="flex-1 relative overflow-hidden flex justify-center bg-gray-900">
                {isPdf && (
                    <div className="h-full overflow-auto p-4 flex justify-center">
                        <Document
                            file={fileUrl}
                            onLoadSuccess={onDocumentLoadSuccess}
                            className="shadow-2xl"
                        >
                            <Page
                                pageNumber={pageNumber}
                                renderTextLayer={false}
                                renderAnnotationLayer={false}
                                className="max-w-full"
                                width={window.innerWidth > 800 ? 800 : window.innerWidth - 40}
                            />
                        </Document>

                        {/* PDF Controls Overlay */}
                        <div className="absolute bottom-8 left-1/2 -translate-x-1/2 bg-gray-800/90 rounded-full px-6 py-2 flex items-center space-x-4 shadow-xl backdrop-blur-sm text-white">
                            <button
                                disabled={pageNumber <= 1}
                                onClick={() => changePage(-1)}
                                className="disabled:opacity-30 hover:text-blue-400"
                            >
                                <ChevronLeft />
                            </button>
                            <span className="font-mono">{pageNumber} / {numPages}</span>
                            <button
                                disabled={pageNumber >= numPages}
                                onClick={() => changePage(1)}
                                className="disabled:opacity-30 hover:text-blue-400"
                            >
                                <ChevronRight />
                            </button>
                        </div>
                    </div>
                )}

                {isEpub && (
                    <div className="h-full w-full max-w-4xl bg-white">
                        <ReactReader
                            url={fileUrl}
                            location={location}
                            locationChanged={locationChanged}
                            epubInitOptions={{
                                openAs: 'epub'
                            }}
                        />
                    </div>
                )}

                {!isPdf && !isEpub && (
                    <div className="flex items-center justify-center h-full text-gray-500">
                        <p>Unsupported format for web reader. Please download to view.</p>
                    </div>
                )}
            </div>
        </div>
    );
}
