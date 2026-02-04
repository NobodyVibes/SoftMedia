import type { VisualizerRenderer } from '../types';

/**
 * Bars Visualizer - Centered vertical frequency bars mirrored from center
 */
export const barsVisualizer: VisualizerRenderer = ({
    ctx,
    frequencyData,
    width,
    height,
    primaryColor,
    secondaryColor,
}) => {
    const barCount = Math.floor(frequencyData.length / 2); // Use half for mirroring
    const centerX = width / 2;
    const barWidth = (width / 2) / barCount; // Half width divided by bar count
    const barGap = 2;

    // Create gradient for bars
    const gradient = ctx.createLinearGradient(0, height, 0, 0);
    gradient.addColorStop(0, primaryColor);
    gradient.addColorStop(1, secondaryColor);

    ctx.fillStyle = gradient;

    // Draw bars mirrored from center
    for (let i = 0; i < barCount; i++) {
        const value = frequencyData[i] / 255;
        const barHeight = value * height * 0.9;
        const y = height - barHeight;

        // Left side (mirrored)
        const xLeft = centerX - (i + 1) * barWidth;
        ctx.fillRect(xLeft + barGap / 2, y, barWidth - barGap, barHeight);

        // Right side (original)
        const xRight = centerX + i * barWidth;
        ctx.fillRect(xRight + barGap / 2, y, barWidth - barGap, barHeight);
    }
};
