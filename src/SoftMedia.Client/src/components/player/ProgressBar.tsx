import React, { useRef, useState, useEffect } from 'react';
import { formatDuration } from '../../lib/utils';
import { type Chapter } from '../../types';

interface ProgressBarProps {
    currentTime: number;
    duration: number;
    bufferedPercent: number;
    chapters?: Chapter[];
    creditsStart?: number;
    onSeek: (time: number) => void;
    onSeekStart: () => void;
    onSeekEnd: () => void;
    framePreviewUrl?: string | null;
}

export function ProgressBar({
    currentTime,
    duration,
    bufferedPercent,
    chapters,
    creditsStart,
    onSeek,
    onSeekStart,
    onSeekEnd,
    framePreviewUrl
}: ProgressBarProps) {
    const progressRef = useRef<HTMLDivElement>(null);
    const [isDragging, setIsDragging] = useState(false);
    const [hoverTime, setHoverTime] = useState<number | null>(null);
    const [hoverPosition, setHoverPosition] = useState(0);
    const [frameLoaded, setFrameLoaded] = useState(false);

    // Calculate progress percentage
    // If dragging, use hoverTime for visual feedback, otherwise use currentTime
    const progressPercent = duration > 0
        ? ((isDragging && hoverTime !== null ? hoverTime : currentTime) / duration) * 100
        : 0;

    const handleProgressMouseDown = (e: React.MouseEvent<HTMLDivElement>) => {
        if (!progressRef.current || duration <= 0) return;

        setIsDragging(true);
        onSeekStart();

        const rect = progressRef.current.getBoundingClientRect();
        const percent = Math.max(0, Math.min(1, (e.clientX - rect.left) / rect.width));
        const time = percent * duration;

        setHoverTime(time);
        setHoverPosition(e.clientX - rect.left);
        onSeek(time); // Instant seek while dragging (optional, or wait for mouse up)
    };

    const handleProgressMouseMove = (e: React.MouseEvent<HTMLDivElement>) => {
        if (!progressRef.current || duration <= 0) return;

        const rect = progressRef.current.getBoundingClientRect();
        const position = e.clientX - rect.left;
        const percent = Math.max(0, Math.min(1, position / rect.width));
        const time = percent * duration;

        setHoverTime(time);
        setHoverPosition(position);

        if (isDragging) {
            onSeek(time);
        }
    };

    const handleProgressMouseLeave = () => {
        if (!isDragging) {
            setHoverTime(null);
            setFrameLoaded(false);
        }
    };

    const handleMouseUp = () => {
        if (isDragging) {
            setIsDragging(false);
            onSeekEnd();
        }
    };

    // Global mouse up listener when dragging
    useEffect(() => {
        if (isDragging) {
            window.addEventListener('mouseup', handleMouseUp);
            return () => window.removeEventListener('mouseup', handleMouseUp);
        }
    }, [isDragging]);

    const getChapterAtTime = (time: number): string | null => {
        if (!chapters || chapters.length === 0) return null;
        // Find the chapter that immediately precedes the current time
        for (let i = chapters.length - 1; i >= 0; i--) {
            if (time >= chapters[i].startTime) {
                return chapters[i].title;
            }
        }
        return null;
    };

    return (
        <div className="relative mb-3 select-none">
            {/* Hover/Drag Tooltip */}
            {hoverTime !== null && progressRef.current && (
                <div
                    className="absolute bottom-full mb-2 transform -translate-x-1/2 pointer-events-none z-10 flex flex-col items-center"
                    style={{ left: Math.max(80, Math.min(hoverPosition, progressRef.current.getBoundingClientRect().width - 80)) }}
                >
                    {/* Frame preview thumbnail */}
                    {isDragging && framePreviewUrl && (
                        <div className="mb-2 rounded overflow-hidden shadow-lg border border-white/20 bg-black/80 min-w-40 min-h-24 flex items-center justify-center">
                            <img
                                src={framePreviewUrl}
                                alt="Frame preview"
                                referrerPolicy="no-referrer"
                                className="w-40 h-auto"
                                onLoad={() => setFrameLoaded(true)}
                            />
                        </div>
                    )}
                    {/* Loading placeholder */}
                    {isDragging && !framePreviewUrl && !frameLoaded && (
                        <div className="mb-2 w-40 h-24 bg-black/80 rounded flex items-center justify-center border border-white/20">
                            <div className="text-white/50 text-xs">Loading...</div>
                        </div>
                    )}

                    {/* Time and chapter label */}
                    <div className="bg-black/90 text-white text-sm px-2 py-1 rounded whitespace-nowrap border border-white/10 shadow-lg">
                        <span className="font-medium text-blue-400">{formatDuration(hoverTime)}</span>
                        {getChapterAtTime(hoverTime) && (
                            <span className="text-gray-300 ml-2 border-l border-white/20 pl-2">{getChapterAtTime(hoverTime)}</span>
                        )}
                    </div>
                </div>
            )}

            {/* Progress bar track */}
            <div
                ref={progressRef}
                className="relative w-full h-1.5 bg-white/20 rounded-full cursor-pointer group/progress hover:h-2.5 transition-all duration-200 ease-out"
                onMouseDown={handleProgressMouseDown}
                onMouseMove={handleProgressMouseMove}
                onMouseLeave={handleProgressMouseLeave}
            >
                {/* Buffered progress */}
                <div
                    className="absolute top-0 left-0 h-full bg-white/30 rounded-full pointer-events-none transition-all duration-300"
                    style={{ width: `${Math.min(bufferedPercent, 100)}%` }}
                />

                {/* Played progress */}
                <div
                    className="absolute top-0 left-0 h-full bg-gradient-to-r from-blue-600 to-blue-400 rounded-full pointer-events-none transition-all duration-100 ease-linear"
                    style={{ width: `${Math.min(progressPercent, 100)}%` }}
                />

                {/* Chapter markers */}
                {chapters && duration > 0 && chapters.map((chapter, idx) => {
                    const isCredits = chapter.title?.toLowerCase().includes('credit') ||
                        chapter.title?.toLowerCase().includes('end') ||
                        chapter.title?.toLowerCase().includes('outro');

                    const percent = (chapter.startTime / duration) * 100;
                    if (percent < 0 || percent > 100) return null;

                    return (
                        <div
                            key={idx}
                            className="absolute top-0 h-full group/chapter z-10"
                            style={{ left: `${percent}%` }}
                        >
                            {/* Visible marker line */}
                            <div className={`w-0.5 h-full ${isCredits ? 'bg-yellow-400' : 'bg-white/50 group-hover/progress:bg-white/80'} shadow-sm`} />

                            {/* Larger invisible hit area for hover tooltips */}
                            <div className="absolute top-0 -left-1 w-2 h-full cursor-pointer" />

                            {/* Chapter Marker Tooltip (small dot or label on hover) */}
                            <div className="absolute bottom-full left-1/2 -translate-x-1/2 mb-3 bg-black/90 text-white text-[10px] px-1.5 py-0.5 rounded opacity-0 group-hover/chapter:opacity-100 transition-opacity pointer-events-none whitespace-nowrap border border-white/10">
                                {chapter.title}
                            </div>
                        </div>
                    );
                })}

                {/* Credits Start Marker (Fallback if no chapters) */}
                {!chapters && creditsStart && duration > 0 && (
                    <div
                        className="absolute top-0 h-full w-0.5 bg-yellow-400/80 z-10"
                        style={{ left: `${(creditsStart / duration) * 100}%` }}
                        title="Credits Start"
                    />
                )}

                {/* Scrubber ball */}
                <div
                    className={`absolute top-1/2 -translate-y-1/2 w-4 h-4 bg-blue-500 rounded-full shadow-[0_0_10px_rgba(59,130,246,0.5)] cursor-grab active:cursor-grabbing border-2 border-white transition-transform hover:scale-110 z-20 ${isDragging ? 'scale-110' : ''
                        }`}
                    style={{ left: `calc(${Math.min(progressPercent, 100)}% - 8px)` }}
                />
            </div>
        </div>
    );
}
