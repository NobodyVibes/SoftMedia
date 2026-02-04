import React, { useEffect, useRef, useState } from 'react';
import { motion, useAnimationControls } from 'framer-motion';
import { cn } from '../../lib/utils';

interface ScrollingTextProps {
    text: string;
    className?: string;
    hoverOnly?: boolean;
    pauseDuration?: number; // seconds
    speed?: number; // pixels per second
}

export const ScrollingText: React.FC<ScrollingTextProps> = ({
    text,
    className,
    hoverOnly = false,
    pauseDuration = 6,
    speed = 30
}) => {
    const containerRef = useRef<HTMLDivElement>(null);
    const textRef = useRef<HTMLDivElement>(null);
    const [shouldScroll, setShouldScroll] = useState(false);
    const [contentWidth, setContentWidth] = useState(0);
    const controls = useAnimationControls();
    const [isHovered, setIsHovered] = useState(false);

    const GAP = 48; // Space between duplicate text for loop (px)

    useEffect(() => {
        if (containerRef.current && textRef.current) {
            const tWidth = textRef.current.offsetWidth;
            const cWidth = containerRef.current.offsetWidth;

            setContentWidth(tWidth);

            if (tWidth > cWidth) {
                setShouldScroll(true);
            } else {
                setShouldScroll(false);
                controls.set({ x: 0 });
            }
        }
    }, [text, controls]);

    useEffect(() => {
        if (!shouldScroll) {
            controls.set({ x: 0 });
            return;
        }

        let isMounted = true;

        // Active if it's always-on mode OR if it's hover-mode and currently hovered
        const isActive = !hoverOnly || isHovered;

        if (!isActive) {
            // Reset to start/stop when not active
            controls.start({
                x: 0,
                transition: { duration: 0.5, ease: "easeOut" }
            });
            return;
        }

        const startScrollLoop = async () => {
            const loopDistance = contentWidth + GAP;
            const duration = loopDistance / speed;

            while (isMounted) {
                // 1. Reset to start
                controls.set({ x: 0 });

                // 2. Pause
                // Use a shorter pause for hover-start/loop (1s) to be responsive,
                // but keep the full pause for the persistent active track.
                const currentPause = hoverOnly ? 1 : pauseDuration;

                await new Promise(resolve => setTimeout(resolve, currentPause * 1000));
                if (!isMounted) break;

                // 3. Animate full loop
                await controls.start({
                    x: -loopDistance,
                    transition: { duration, ease: "linear" }
                });

                // 4. Loop repeats immediately (resetting to 0 at top of loop)
                if (!isMounted) break;
            }
        };

        // Stop any current animation before starting new loop logic
        controls.stop();
        startScrollLoop();

        return () => {
            isMounted = false;
            controls.stop();
        };
    }, [shouldScroll, contentWidth, hoverOnly, isHovered, pauseDuration, speed, controls, GAP]);

    return (
        <div
            ref={containerRef}
            className={cn("overflow-hidden whitespace-nowrap mask-gradient", className)}
            onMouseEnter={() => hoverOnly && setIsHovered(true)}
            onMouseLeave={() => hoverOnly && setIsHovered(false)}
        >
            <motion.div
                animate={controls}
                className="flex items-center"
            >
                <div ref={textRef} className="shrink-0">
                    {text}
                </div>

                {/* Render duplicate for seamless looping if scroll is needed, regardless of hover mode */}
                {shouldScroll && (
                    <>
                        <div style={{ width: GAP }} className="shrink-0" />
                        <div className="shrink-0">
                            {text}
                        </div>
                    </>
                )}
            </motion.div>
        </div>
    );
};
