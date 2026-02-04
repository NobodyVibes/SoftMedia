import type { VisualizerRenderer } from '../types';

/**
 * Circular Visualizer - Radial frequency bars around center
 */
export const circularVisualizer: VisualizerRenderer = ({
    ctx,
    frequencyData,
    width,
    height,
    primaryColor,
    secondaryColor,
}) => {
    const centerX = width / 2;
    const centerY = height / 2;
    const radius = Math.min(width, height) * 0.25;
    const barCount = frequencyData.length;

    // Create radial gradient
    const gradient = ctx.createRadialGradient(
        centerX, centerY, radius * 0.5,
        centerX, centerY, radius * 2
    );
    gradient.addColorStop(0, primaryColor);
    gradient.addColorStop(1, secondaryColor);

    ctx.strokeStyle = gradient;
    ctx.lineWidth = 3;
    ctx.lineCap = 'round';

    for (let i = 0; i < barCount; i++) {
        const value = frequencyData[i] / 255;
        const angle = (i / barCount) * Math.PI * 2 - Math.PI / 2;

        const innerRadius = radius;
        const outerRadius = radius + value * radius * 1.5;

        const x1 = centerX + Math.cos(angle) * innerRadius;
        const y1 = centerY + Math.sin(angle) * innerRadius;
        const x2 = centerX + Math.cos(angle) * outerRadius;
        const y2 = centerY + Math.sin(angle) * outerRadius;

        ctx.beginPath();
        ctx.moveTo(x1, y1);
        ctx.lineTo(x2, y2);
        ctx.stroke();
    }

    // Draw center circle
    ctx.beginPath();
    ctx.arc(centerX, centerY, radius * 0.3, 0, Math.PI * 2);
    ctx.fillStyle = `${primaryColor}33`;
    ctx.fill();
};
