import type { VisualizerRenderer } from '../types';

// Persistent particle state (persists across renders)
interface Particle {
    x: number;
    y: number;
    vx: number;
    vy: number;
    size: number;
    alpha: number;
    hue: number;
}

let particles: Particle[] = [];
let lastWidth = 0;
let lastHeight = 0;

const PARTICLE_COUNT = 50;

/**
 * Particles Visualizer - Audio-reactive floating particles
 */
export const particlesVisualizer: VisualizerRenderer = ({
    ctx,
    frequencyData,
    width,
    height,
    primaryColor,
    secondaryColor,
}) => {
    // Reset particles if canvas size changed
    if (width !== lastWidth || height !== lastHeight) {
        particles = [];
        lastWidth = width;
        lastHeight = height;
    }

    // Initialize particles if needed
    while (particles.length < PARTICLE_COUNT) {
        particles.push({
            x: Math.random() * width,
            y: Math.random() * height,
            vx: (Math.random() - 0.5) * 2,
            vy: (Math.random() - 0.5) * 2,
            size: Math.random() * 4 + 2,
            alpha: Math.random() * 0.5 + 0.3,
            hue: Math.random(),
        });
    }

    // Calculate average amplitude
    let sum = 0;
    for (let i = 0; i < frequencyData.length; i++) {
        sum += frequencyData[i];
    }
    const avgAmplitude = sum / frequencyData.length / 255;

    // Get bass (low frequencies) for more punch
    const bassAmplitude = (frequencyData[0] + frequencyData[1] + frequencyData[2]) / 3 / 255;

    // Update and draw particles
    for (const particle of particles) {
        // Audio-reactive velocity boost
        const boost = 1 + bassAmplitude * 3;

        // Update position
        particle.x += particle.vx * boost;
        particle.y += particle.vy * boost;

        // Wrap around edges
        if (particle.x < 0) particle.x = width;
        if (particle.x > width) particle.x = 0;
        if (particle.y < 0) particle.y = height;
        if (particle.y > height) particle.y = 0;

        // Audio-reactive size
        const size = particle.size * (1 + avgAmplitude * 2);

        // Interpolate between primary and secondary color based on hue.
        // Opacity comes from globalAlpha rather than an appended "##" hex pair:
        // the theme vars only happen to be hex today, and any other notation
        // (oklch, rgb) would make addColorStop throw and blank the frame.
        const color = particle.hue < 0.5 ? primaryColor : secondaryColor;
        const gradient = ctx.createRadialGradient(
            particle.x, particle.y, 0,
            particle.x, particle.y, size * 2
        );
        gradient.addColorStop(0, color);
        gradient.addColorStop(1, 'transparent');

        ctx.globalAlpha = particle.alpha;
        ctx.beginPath();
        ctx.arc(particle.x, particle.y, size * 2, 0, Math.PI * 2);
        ctx.fillStyle = gradient;
        ctx.fill();

        // Draw core
        ctx.beginPath();
        ctx.arc(particle.x, particle.y, size, 0, Math.PI * 2);
        ctx.fillStyle = color;
        ctx.globalAlpha = particle.alpha * (0.5 + avgAmplitude);
        ctx.fill();
        ctx.globalAlpha = 1;
    }
};
