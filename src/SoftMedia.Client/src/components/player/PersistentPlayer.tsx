import React, { useEffect, useRef, useState } from 'react';
import { useAudioStore } from '../../store/audioStore';
import { Play, Pause, SkipForward, SkipBack, Volume2, VolumeX, Maximize2 } from 'lucide-react';
import { API_URL } from '../../services/api';

export const PersistentPlayer: React.FC = () => {
    const { currentTrack, isPlaying, volume, isMuted, pause, resume, next, previous, setVolume, toggleMute } = useAudioStore();
    const audioRef = useRef<HTMLAudioElement>(null);
    const [progress, setProgress] = useState(0);
    const [duration, setDuration] = useState(0);

    useEffect(() => {
        if (audioRef.current) {
            if (isPlaying) {
                audioRef.current.play().catch(e => console.error("Playback failed", e));
            } else {
                audioRef.current.pause();
            }
        }
    }, [isPlaying, currentTrack]);

    useEffect(() => {
        if (audioRef.current) {
            audioRef.current.volume = isMuted ? 0 : volume;
        }
    }, [volume, isMuted]);

    const handleTimeUpdate = () => {
        if (audioRef.current) {
            setProgress(audioRef.current.currentTime);
            setDuration(audioRef.current.duration || 0);
        }
    };

    const handleSeek = (e: React.ChangeEvent<HTMLInputElement>) => {
        const time = parseFloat(e.target.value);
        if (audioRef.current) {
            audioRef.current.currentTime = time;
            setProgress(time);
        }
    };

    const handleEnded = () => {
        next();
    };

    if (!currentTrack) return null;

    // Construct stream URL
    const streamUrl = `${API_URL}/stream/${currentTrack.id}`;
    const imageUrl = currentTrack.posterPath ? `${API_URL}${currentTrack.posterPath}` : '/placeholder-music.png';

    return (
        <div className="fixed bottom-0 left-0 right-0 h-20 bg-gray-900 border-t border-gray-800 flex items-center px-4 z-50 shadow-2xl">
            <audio
                ref={audioRef}
                src={streamUrl}
                onTimeUpdate={handleTimeUpdate}
                onEnded={handleEnded}
            />

            {/* Track Info */}
            <div className="flex items-center w-1/4 min-w-[200px]">
                <img
                    src={imageUrl}
                    alt={currentTrack.title}
                    className="w-14 h-14 rounded object-cover mr-4 bg-gray-800"
                />
                <div className="truncate">
                    <h4 className="text-white font-medium truncate">{currentTrack.title}</h4>
                    <p className="text-gray-400 text-sm truncate">{currentTrack.description || 'Unknown Artist'}</p>
                </div>
            </div>

            {/* Controls */}
            <div className="flex-1 flex flex-col items-center justify-center">
                <div className="flex items-center space-x-6 mb-1">
                    <button onClick={previous} className="text-gray-400 hover:text-white transition">
                        <SkipBack size={20} />
                    </button>
                    <button
                        onClick={isPlaying ? pause : resume}
                        className="w-10 h-10 rounded-full bg-white text-black flex items-center justify-center hover:scale-105 transition"
                    >
                        {isPlaying ? <Pause size={20} fill="currentColor" /> : <Play size={20} fill="currentColor" className="ml-1" />}
                    </button>
                    <button onClick={next} className="text-gray-400 hover:text-white transition">
                        <SkipForward size={20} />
                    </button>
                </div>

                {/* Progress Bar */}
                <div className="w-full max-w-md flex items-center space-x-2 text-xs text-gray-400">
                    <span>{formatTime(progress)}</span>
                    <input
                        type="range"
                        min="0"
                        max={duration || 100}
                        value={progress}
                        onChange={handleSeek}
                        className="flex-1 h-1 bg-gray-700 rounded-lg appearance-none cursor-pointer [&::-webkit-slider-thumb]:appearance-none [&::-webkit-slider-thumb]:w-3 [&::-webkit-slider-thumb]:h-3 [&::-webkit-slider-thumb]:bg-white [&::-webkit-slider-thumb]:rounded-full"
                    />
                    <span>{formatTime(duration)}</span>
                </div>
            </div>

            {/* Volume & Extras */}
            <div className="w-1/4 flex items-center justify-end space-x-4">
                <button onClick={toggleMute} className="text-gray-400 hover:text-white">
                    {isMuted ? <VolumeX size={20} /> : <Volume2 size={20} />}
                </button>
                <input
                    type="range"
                    min="0"
                    max="1"
                    step="0.01"
                    value={isMuted ? 0 : volume}
                    onChange={(e) => setVolume(parseFloat(e.target.value))}
                    className="w-24 h-1 bg-gray-700 rounded-lg appearance-none cursor-pointer [&::-webkit-slider-thumb]:appearance-none [&::-webkit-slider-thumb]:w-3 [&::-webkit-slider-thumb]:h-3 [&::-webkit-slider-thumb]:bg-white [&::-webkit-slider-thumb]:rounded-full"
                />
                <button className="text-gray-400 hover:text-white">
                    <Maximize2 size={18} />
                </button>
            </div>
        </div>
    );
};

const formatTime = (seconds: number) => {
    if (!seconds) return "0:00";
    const mins = Math.floor(seconds / 60);
    const secs = Math.floor(seconds % 60);
    return `${mins}:${secs.toString().padStart(2, '0')}`;
};
