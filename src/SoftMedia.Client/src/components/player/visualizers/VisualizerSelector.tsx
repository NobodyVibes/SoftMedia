import React, { useState, useRef, useEffect } from 'react';
import { createPortal } from 'react-dom';
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
    const buttonRef = useRef<HTMLButtonElement>(null);
    const { activeVisualizer, setActiveVisualizer, isEnabled } = useVisualizerStore();
    const [coords, setCoords] = useState({ top: 0, left: 0, width: 0 });

    const updatePosition = () => {
        if (buttonRef.current) {
            const rect = buttonRef.current.getBoundingClientRect();
            setCoords({
                top: direction === 'up' ? rect.top : rect.bottom,
                left: rect.left,
                width: rect.width
            });
        }
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

    if (!isEnabled) return null;

    const currentOption = VISUALIZER_OPTIONS.find(opt => opt.id === activeVisualizer);

    return (
        <div className={cn("relative inline-block", className)}>
            <button
                ref={buttonRef}
                onClick={() => {
                    updatePosition();
                    setIsOpen(!isOpen);
                }}
                className="flex items-center gap-1 px-3 py-1.5 bg-white/10 hover:bg-white/20 rounded-md text-sm text-white transition"
            >
                <span>{currentOption?.name || 'Bars'}</span>
                <ChevronDown size={14} className={cn("transition-transform", isOpen && "rotate-180")} />
            </button>

            {isOpen && createPortal(
                <div
                    ref={dropdownRef}
                    className={cn(
                        "fixed bg-gray-900 border border-gray-700 rounded-md shadow-2xl overflow-hidden z-[9999] min-w-[120px]",
                        // Basic positioning
                    )}
                    style={{
                        top: direction === 'up' ? 'auto' : coords.top + 4,
                        bottom: direction === 'up' ? (window.innerHeight - coords.top) + 4 : 'auto',
                        left: coords.left,
                    }}
                >
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
                </div>,
                document.body
            )}
        </div>
    );
};
