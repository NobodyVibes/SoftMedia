import React, { useState, useRef, useEffect } from 'react';
import { ChevronDown } from 'lucide-react';
import { useVisualizerStore, type VisualizerType } from '../../../store/visualizerStore';
import { cn } from '../../../lib/utils';

const VISUALIZER_OPTIONS: { id: VisualizerType; name: string }[] = [
    { id: 'bars', name: 'Bars' },
    { id: 'waveform', name: 'Waveform' },
    { id: 'circular', name: 'Circular' },
    { id: 'particles', name: 'Particles' },
];

interface VisualizerSelectorProps {
    className?: string;
    direction?: 'up' | 'down';
}

export const VisualizerSelector: React.FC<VisualizerSelectorProps> = ({ className, direction = 'down' }) => {
    const [isOpen, setIsOpen] = useState(false);
    const dropdownRef = useRef<HTMLDivElement>(null);
    const { activeVisualizer, setActiveVisualizer, isEnabled } = useVisualizerStore();

    // Close dropdown when clicking outside
    useEffect(() => {
        const handleClickOutside = (e: MouseEvent) => {
            if (dropdownRef.current && !dropdownRef.current.contains(e.target as Node)) {
                setIsOpen(false);
            }
        };

        document.addEventListener('mousedown', handleClickOutside);
        return () => document.removeEventListener('mousedown', handleClickOutside);
    }, []);

    if (!isEnabled) return null;

    const currentOption = VISUALIZER_OPTIONS.find(opt => opt.id === activeVisualizer);

    return (
        <div ref={dropdownRef} className={cn("relative", className)}>
            <button
                onClick={() => setIsOpen(!isOpen)}
                className="flex items-center gap-1 px-3 py-1.5 bg-white/10 hover:bg-white/20 rounded-md text-sm text-white transition"
            >
                <span>{currentOption?.name || 'Bars'}</span>
                <ChevronDown size={14} className={cn("transition-transform", isOpen && "rotate-180")} />
            </button>

            {isOpen && (
                <div className={cn(
                    "absolute left-0 bg-gray-800 border border-gray-700 rounded-md shadow-xl overflow-hidden z-50 min-w-[120px]",
                    direction === 'up' ? 'bottom-full mb-1' : 'top-full mt-1'
                )}>
                    {VISUALIZER_OPTIONS.map((option) => (
                        <button
                            key={option.id}
                            onClick={() => {
                                setActiveVisualizer(option.id);
                                setIsOpen(false);
                            }}
                            className={cn(
                                "w-full text-left px-3 py-2 text-sm transition",
                                option.id === activeVisualizer
                                    ? "bg-primary text-white"
                                    : "text-gray-300 hover:bg-gray-700"
                            )}
                        >
                            {option.name}
                        </button>
                    ))}
                </div>
            )}
        </div>
    );
};
