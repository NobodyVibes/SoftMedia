import { useNavigate } from 'react-router-dom';
import { useQuery } from '@tanstack/react-query';
import api from '../../services/api';
import { type MediaItem, type PagedResult } from '../../types';
import { Camera, MapPin, Aperture, Clock, ChevronLeft, ChevronRight } from 'lucide-react';

interface PhotoDetailViewProps {
    item: MediaItem;
}

export default function PhotoDetailView({ item }: PhotoDetailViewProps) {
    const navigate = useNavigate();

    // Fetch library items for navigation (Limit to 1000 for now)
    const { data: libraryItems } = useQuery({
        queryKey: ['library', item.libraryId, 'items', 'navigation'],
        queryFn: async () => {
            const response = await api.get<PagedResult<MediaItem>>(`/libraries/${item.libraryId}/items`, {
                params: { page: 1, pageSize: 1000 }
            });
            return response.data.items;
        },
        enabled: !!item.libraryId
    });

    const currentIndex = libraryItems?.findIndex(i => i.id === item.id) ?? -1;
    const prevItem = currentIndex > 0 ? libraryItems?.[currentIndex - 1] : null;
    const nextItem = currentIndex !== -1 && currentIndex < (libraryItems?.length ?? 0) - 1 ? libraryItems?.[currentIndex + 1] : null;

    const metadata = item.metadata || {};
    const camera = metadata.camera as string;
    const iso = metadata.iso as string;
    const fstop = metadata.fstop as string;
    const exposure = metadata.exposure as string;
    const dateTaken = metadata.dateTaken as string;
    const gps = metadata.gps as string;

    return (
        <div className="space-y-8">
            <div className="grid grid-cols-1 md:grid-cols-2 lg:grid-cols-4 gap-4">
                {camera && (
                    <div className="bg-white/5 p-4 rounded-xl border border-white/10 flex items-center gap-3">
                        <Camera className="w-5 h-5 text-blue-400" />
                        <div>
                            <div className="text-xs text-gray-400 uppercase">Camera</div>
                            <div className="text-white font-medium">{camera}</div>
                        </div>
                    </div>
                )}
                {(iso || fstop || exposure) && (
                    <div className="bg-white/5 p-4 rounded-xl border border-white/10 flex items-center gap-3">
                        <Aperture className="w-5 h-5 text-yellow-400" />
                        <div>
                            <div className="text-xs text-gray-400 uppercase">Settings</div>
                            <div className="text-white font-medium">
                                {[fstop && `f/${fstop}`, exposure && `${exposure}s`, iso && `ISO ${iso}`].filter(Boolean).join(' • ')}
                            </div>
                        </div>
                    </div>
                )}
                {dateTaken && (
                    <div className="bg-white/5 p-4 rounded-xl border border-white/10 flex items-center gap-3">
                        <Clock className="w-5 h-5 text-green-400" />
                        <div>
                            <div className="text-xs text-gray-400 uppercase">Date Taken</div>
                            <div className="text-white font-medium">{dateTaken}</div>
                        </div>
                    </div>
                )}
                {gps && (
                    <div className="bg-white/5 p-4 rounded-xl border border-white/10 flex items-center gap-3">
                        <MapPin className="w-5 h-5 text-red-400" />
                        <div>
                            <div className="text-xs text-gray-400 uppercase">Location</div>
                            <div className="text-white font-medium truncate" title={gps}>{gps}</div>
                        </div>
                    </div>
                )}
            </div>

            {/* Navigation Buttons */}
            <div className="flex justify-between items-center pt-8 border-t border-white/10">
                {prevItem ? (
                    <button
                        onClick={() => navigate(`/media/${prevItem!.id}`)}
                        className="flex items-center gap-2 text-gray-400 hover:text-white transition-colors group"
                    >
                        <ChevronLeft className="w-5 h-5 group-hover:-translate-x-1 transition-transform" />
                        <div className="text-left">
                            <div className="text-xs text-gray-500">Previous</div>
                            <div className="font-medium max-w-[150px] truncate">{prevItem.title}</div>
                        </div>
                    </button>
                ) : <div />}

                {nextItem && (
                    <button
                        onClick={() => navigate(`/media/${nextItem!.id}`)}
                        className="flex items-center gap-2 text-gray-400 hover:text-white transition-colors group text-right"
                    >
                        <div className="text-right">
                            <div className="text-xs text-gray-500">Next</div>
                            <div className="font-medium max-w-[150px] truncate">{nextItem.title}</div>
                        </div>
                        <ChevronRight className="w-5 h-5 group-hover:translate-x-1 transition-transform" />
                    </button>
                )}
            </div>
        </div >
    );
}
