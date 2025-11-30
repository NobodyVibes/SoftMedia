import { MediaPlayer, MediaOutlet, MediaPoster } from '@vidstack/react';
import 'vidstack/styles/base.css';
import 'vidstack/styles/community-skin/video.css';
import { type MediaItem } from '../../types';

interface VideoPlayerProps {
    item: MediaItem;
    src: string;
}

export default function VideoPlayer({ item, src }: VideoPlayerProps) {
    return (
        <div className="w-full max-w-5xl mx-auto aspect-video bg-black rounded-xl overflow-hidden shadow-2xl">
            <MediaPlayer
                title={item.title}
                src={src}
                aspectRatio="16/9"
                load="eager"
            >
                <MediaOutlet>
                    {item.posterPath && (
                        <MediaPoster
                            alt={item.title}
                            src={`/api/v1/media/${item.id}/poster`}
                        />
                    )}
                </MediaOutlet>
            </MediaPlayer>
        </div>
    );
}
