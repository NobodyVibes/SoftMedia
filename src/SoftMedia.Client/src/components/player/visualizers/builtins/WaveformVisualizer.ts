import type { VisualizerRenderer } from '../types';

/**
 * Waveform Visualizer - Oscilloscope-style waveform line
 */
export const waveformVisualizer: VisualizerRenderer = ({
    ctx,
    timeDomainData,
    width,
    height,
    primaryColor,
    secondaryColor,
}) => {
    const bufferLength = timeDomainData.length;
    // Calculate slice width to span exactly to the right edge
    // Use (bufferLength - 1) because N points create N-1 segments
    const sliceWidth = width / (bufferLength - 1);

    // Create gradient for line
    const gradient = ctx.createLinearGradient(0, 0, width, 0);
    gradient.addColorStop(0, primaryColor);
    gradient.addColorStop(1, secondaryColor);

    ctx.strokeStyle = gradient;
    ctx.lineWidth = 3;
    ctx.lineCap = 'round';
    ctx.lineJoin = 'round';

    ctx.beginPath();

    let x = 0;
    for (let i = 0; i < bufferLength; i++) {
        // Convert 0-255 to -1 to 1, then to y position
        const v = (timeDomainData[i] / 128.0) - 1;
        const y = (height / 2) + (v * height / 2 * 0.8);

        if (i === 0) {
            ctx.moveTo(x, y);
        } else {
            ctx.lineTo(x, y);
        }

        x += sliceWidth;
    }

    ctx.stroke();

    // Add glow effect
    ctx.shadowBlur = 10;
    ctx.shadowColor = primaryColor;
    ctx.stroke();
    ctx.shadowBlur = 0;
};
