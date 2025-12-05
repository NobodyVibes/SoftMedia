import { type MediaItem } from '../../types';
import { Gamepad2 } from 'lucide-react';

interface GameDetailViewProps {
    item: MediaItem;
}

export default function GameDetailView({ item }: GameDetailViewProps) {
    const metadata = item.metadata || {};
    const platform = metadata.platform as string;
    const developer = metadata.studio as string; // Mapped from developer
    const publisher = metadata.publisher as string;
    const mode = metadata.gameMode as string;

    return (
        <div className="space-y-8">
            <div className="flex items-center gap-4 mb-6">
                <div className="p-3 bg-green-500/20 rounded-lg">
                    <Gamepad2 className="w-8 h-8 text-green-400" />
                </div>
                {platform && (
                    <h2 className="text-2xl font-bold text-white">{platform}</h2>
                )}
            </div>

            <div className="grid grid-cols-1 md:grid-cols-2 lg:grid-cols-3 gap-8">
                {developer && (
                    <div>
                        <h3 className="text-gray-400 text-sm uppercase tracking-wider mb-1">Developer</h3>
                        <p className="text-white text-lg">{developer}</p>
                    </div>
                )}
                {publisher && (
                    <div>
                        <h3 className="text-gray-400 text-sm uppercase tracking-wider mb-1">Publisher</h3>
                        <p className="text-white text-lg">{publisher}</p>
                    </div>
                )}
                {mode && (
                    <div>
                        <h3 className="text-gray-400 text-sm uppercase tracking-wider mb-1">Game Mode</h3>
                        <p className="text-white text-lg">{mode}</p>
                    </div>
                )}
            </div>
        </div>
    );
}
