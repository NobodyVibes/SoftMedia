/**
 * R-WI-018 — subtitle appearance via the `::cue` pseudo-element (the chosen
 * design: it styles the native renderer the HLS sidecar-VTT path already uses;
 * a custom cue renderer was rejected as out of proportion for v1). `::cue`
 * supports color/background/font-size/text-shadow — enough for size, color,
 * background opacity, and edge style. Limits (font family, exact positioning)
 * are accepted v1 constraints.
 */

export interface SubtitleAppearance {
    /** Percent of the browser's default cue size: '75' | '100' | '125' | '150'. */
    fontSize: string;
    /** Text color name from the fixed palette. */
    color: string;
    /** Background opacity: '0' | '0.5' | '0.75' | '1'. */
    bgOpacity: string;
    /** 'none' | 'outline' | 'shadow'. */
    edgeStyle: string;
}

export const SUBTITLE_COLORS: Record<string, string> = {
    white: '#ffffff',
    yellow: '#ffe14d',
    cyan: '#7fe7ff',
    green: '#8aff9e',
};

const EDGE_SHADOWS: Record<string, string> = {
    none: 'none',
    // 4-direction shadow approximates an outline (::cue has no -webkit-text-stroke).
    outline: '-1px -1px 0 #000, 1px -1px 0 #000, -1px 1px 0 #000, 1px 1px 0 #000',
    shadow: '2px 2px 3px rgba(0,0,0,0.9)',
};

/**
 * Build the stylesheet text for a `<style>` tag. Scoped to the player's video
 * element class so nothing else on the page is affected.
 */
export function buildCueCss(appearance: SubtitleAppearance, videoClass = 'sm-player-video'): string {
    const color = SUBTITLE_COLORS[appearance.color] ?? SUBTITLE_COLORS.white;
    const size = ['75', '100', '125', '150'].includes(appearance.fontSize) ? appearance.fontSize : '100';
    const opacity = ['0', '0.5', '0.75', '1'].includes(appearance.bgOpacity) ? appearance.bgOpacity : '0.75';
    const shadow = EDGE_SHADOWS[appearance.edgeStyle] ?? EDGE_SHADOWS.none;

    return `video.${videoClass}::cue {
  color: ${color};
  background-color: rgba(0, 0, 0, ${opacity});
  font-size: ${size}%;
  text-shadow: ${shadow};
}`;
}
