import { MediaPlayer, MediaOutlet, MediaPoster } from '@vidstack/react';
import { useEffect, useState } from 'react';
import 'vidstack/styles/base.css';
import 'vidstack/styles/community-skin/video.css';
import { type MediaItem } from '../../types';

interface VideoPlayerProps {
    item: MediaItem;
    src: string;
}

export default function VideoPlayer({ item, src: initialSrc }: VideoPlayerProps) {
    const [src, setSrc] = useState(initialSrc);
    const [isTranscoding, setIsTranscoding] = useState(false);

    useEffect(() => {
        const checkSupport = async () => {
            const video = document.createElement('video');
            // Simple check: if it's MKV, most browsers don't support it directly.
            // Also check if the browser can play the specific mime type if available.
            // For now, we'll assume MKV needs transcoding.
            // In a real app, we'd check item.metadata for codecs.

            const isMkv = item.container?.toLowerCase() === 'mkv';
            const canPlay = video.canPlayType(`video/${item.container}`);

            if (isMkv || canPlay === '') {
                console.log(`Format ${item.container} not supported, switching to HLS transcoding.`);
                setSrc(`http://localhost:5011/api/transcode/${item.id}/master.m3u8`);
                setIsTranscoding(true);
            } else {
                setSrc(initialSrc);
                setIsTranscoding(false);
            }
        };

        checkSupport();
    }, [item, initialSrc]);

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
            {isTranscoding && (
                <div className="text-xs text-white/50 text-center mt-2">
                    Transcoding via FFmpeg (HLS)
                </div>
            )}
        </div>
    );
}
