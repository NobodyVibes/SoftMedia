import React, { useState, useRef, useEffect } from 'react';
import { createPortal } from 'react-dom';
import { Activity } from 'lucide-react';
import { useVisualizerStore, type VisualizerType } from '../../../store/visualizerStore';
import { cn } from '../../../lib/utils';

const VISUALIZER_OPTIONS: { id: VisualizerType | 'off'; name: string }[] = [
    { id: 'off', name: 'Off' },
    { id: 'bars', name: 'Bars' },
    { id: 'waveform', name: 'Waveform' },
    { id: 'circular', name: 'Circular' },
    { id: 'particles', name: 'Particles' },
];

interface VisualizerSelectorProps {
    className?: string;
    direction?: 'up' | 'down';
    iconSize?: number;
}

export const VisualizerSelector: React.FC<VisualizerSelectorProps> = ({
    className,
    direction = 'down',
    iconSize = 24
}) => {
    const [isOpen, setIsOpen] = useState(false);
    const dropdownRef = useRef<HTMLDivElement>(null);
    const buttonRef = useRef<HTMLButtonElement>(null);
    const { activeVisualizer, setActiveVisualizer, isEnabled, setEnabled } = useVisualizerStore();
    const [coords, setCoords] = useState({ top: 0, left: 0, width: 0 });

    const updatePosition = () => {
        if (buttonRef.current) {
            const rect = buttonRef.current.getBoundingClientRect();
            // Align dropdown center with button center if possible, or just left align
            // Given the requested behavior is like subtitles in video player, let's stick to simple positioning
            setCoords({
                top: direction === 'up' ? rect.top : rect.bottom,
                left: rect.left, // Left align for now
                width: rect.width
            });
        }
    };

    const handleToggle = () => {
        updatePosition();
        setIsOpen(!isOpen);
    };

    const handleSelect = (id: VisualizerType | 'off') => {
        if (id === 'off') {
            setEnabled(false);
        } else {
            setActiveVisualizer(id);
            setEnabled(true);
        }
        setIsOpen(false);
    };

    useEffect(() => {
        if (isOpen) {
            updatePosition();
            window.addEventListener('resize', updatePosition);
            window.addEventListener('scroll', updatePosition, true);
        }
        return () => {
            window.removeEventListener('resize', updatePosition);
            window.removeEventListener('scroll', updatePosition, true);
        };
    }, [isOpen]);

    // Close dropdown when clicking outside
    useEffect(() => {
        const handleClickOutside = (e: MouseEvent) => {
            if (
                dropdownRef.current && !dropdownRef.current.contains(e.target as Node) &&
                buttonRef.current && !buttonRef.current.contains(e.target as Node)
            ) {
                setIsOpen(false);
            }
        };

        if (isOpen) {
            document.addEventListener('mousedown', handleClickOutside);
        }
        return () => document.removeEventListener('mousedown', handleClickOutside);
    }, [isOpen]);

    // Determine current selection state
    const currentSelection = isEnabled ? activeVisualizer : 'off';

    return (
        <div className={cn("relative inline-block", className)}>
            <button
                ref={buttonRef}
                onClick={handleToggle}
                className={cn(
                    "p-2 transition rounded-full hover:bg-white/10",
                    isEnabled ? "text-primary" : "text-gray-400 hover:text-white"
                )}
                title="Visualizer Settings"
            >
                <Activity size={iconSize} />
            </button>

            {isOpen && createPortal(
                <div
                    ref={dropdownRef}
                    className={cn(
                        "fixed bg-gray-900 border border-gray-700 rounded-md shadow-2xl overflow-hidden z-[9999] min-w-[140px]",
                        "animate-in fade-in zoom-in-95 duration-100"
                    )}
                    style={{
                        top: direction === 'up' ? 'auto' : coords.top + 8,
                        bottom: direction === 'up' ? (window.innerHeight - coords.top) + 8 : 'auto',
                        left: coords.left - (140 / 2) + (coords.width / 2), // Center on any button width
                    }}
                >
                    <div className="py-1">
                        {VISUALIZER_OPTIONS.map((option) => (
                            <button
                                key={option.id}
                                onClick={() => handleSelect(option.id)}
                                className={cn(
                                    "w-full text-left px-4 py-2 text-sm transition flex items-center gap-2",
                                    option.id === currentSelection
                                        ? "bg-primary text-white"
                                        : "text-gray-300 hover:bg-gray-800 hover:text-white"
                                )}
                            >
                                {option.name}
                            </button>
                        ))}
                    </div>
                </div>,
                document.body
            )}
        </div>
    );
};
