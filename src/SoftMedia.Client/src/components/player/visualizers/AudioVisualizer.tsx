import React, { useRef, useEffect, useCallback } from 'react';
import { useVisualizerStore } from '../../../store/visualizerStore';
import type { VisualizerContext, VisualizerRenderer } from './types';
import { barsVisualizer } from './builtins/BarsVisualizer';
import { waveformVisualizer } from './builtins/WaveformVisualizer';
import { circularVisualizer } from './builtins/CircularVisualizer';
import { particlesVisualizer } from './builtins/ParticlesVisualizer';

// Map of built-in visualizers
const VISUALIZERS: Record<string, VisualizerRenderer> = {
    bars: barsVisualizer,
    waveform: waveformVisualizer,
    circular: circularVisualizer,
    particles: particlesVisualizer,
};

// Read CSS variables for theme colors
const getThemeColors = () => {
    const style = getComputedStyle(document.documentElement);
    return {
        primary: style.getPropertyValue('--color-primary').trim() || '#007AFF',
        secondary: style.getPropertyValue('--color-secondary').trim() || '#8A2BE2',
    };
};

interface AudioVisualizerProps {
    frequencyData: Uint8Array;
    timeDomainData: Uint8Array;
    isReady: boolean;
    updateData: () => void;
    className?: string;
}

export const AudioVisualizer: React.FC<AudioVisualizerProps> = ({
    frequencyData,
    timeDomainData,
    isReady,
    updateData,
    className = '',
}) => {
    const canvasRef = useRef<HTMLCanvasElement>(null);
    const animationFrameRef = useRef<number>(0);
    const dimensionsRef = useRef<{ width: number; height: number }>({ width: 0, height: 0 });
    const { isEnabled, activeVisualizer } = useVisualizerStore();

    // Handle canvas resize - store display dimensions separately
    useEffect(() => {
        const canvas = canvasRef.current;
        if (!canvas) return;

        const updateCanvasSize = () => {
            const rect = canvas.getBoundingClientRect();
            const dpr = window.devicePixelRatio || 1;

            // Store display dimensions (CSS pixels)
            dimensionsRef.current = { width: rect.width, height: rect.height };

            // Set canvas buffer size (device pixels)
            canvas.width = rect.width * dpr;
            canvas.height = rect.height * dpr;

            // Scale context for HiDPI
            const ctx = canvas.getContext('2d');
            if (ctx) {
                ctx.setTransform(dpr, 0, 0, dpr, 0, 0);
            }
        };

        // Initial size
        updateCanvasSize();

        const resizeObserver = new ResizeObserver(() => {
            updateCanvasSize();
        });

        resizeObserver.observe(canvas);

        return () => resizeObserver.disconnect();
    }, [isEnabled]); // Re-run when visualizer is enabled to ensure dimensions are captured

    // Animation loop
    const animate = useCallback(() => {
        const canvas = canvasRef.current;
        if (!canvas || !isEnabled || !isReady) return;

        // Update audio data buffers
        updateData();

        const ctx = canvas.getContext('2d');
        if (!ctx) return;

        // Get current renderer
        const renderer = VISUALIZERS[activeVisualizer] || VISUALIZERS.bars;

        // Get theme colors
        const colors = getThemeColors();

        // Use stored display dimensions (CSS pixels, not canvas buffer size)
        const { width, height } = dimensionsRef.current;

        // Create context for renderer
        const visualizerContext: VisualizerContext = {
            canvas,
            ctx,
            frequencyData,
            timeDomainData,
            width,
            height,
            primaryColor: colors.primary,
            secondaryColor: colors.secondary,
        };

        // Clear canvas using display dimensions
        ctx.clearRect(0, 0, width, height);

        // Render visualization
        renderer(visualizerContext);

        // Continue animation loop
        animationFrameRef.current = requestAnimationFrame(animate);
    }, [frequencyData, timeDomainData, isEnabled, isReady, activeVisualizer, updateData]);

    // Start/stop animation based on enabled state
    useEffect(() => {
        if (isEnabled && isReady) {
            animationFrameRef.current = requestAnimationFrame(animate);
        }

        return () => {
            if (animationFrameRef.current) {
                cancelAnimationFrame(animationFrameRef.current);
            }
        };
    }, [isEnabled, isReady, animate]);

    if (!isEnabled) return null;

    return (
        <canvas
            ref={canvasRef}
            className={`absolute inset-0 w-full h-full pointer-events-none ${className}`}
            style={{ opacity: 0.6 }}
        />
    );
};
