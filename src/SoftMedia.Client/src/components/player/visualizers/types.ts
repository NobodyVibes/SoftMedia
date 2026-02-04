/**
 * Visualizer type definitions
 */

export interface VisualizerContext {
    canvas: HTMLCanvasElement;
    ctx: CanvasRenderingContext2D;
    frequencyData: Uint8Array;
    timeDomainData: Uint8Array;
    width: number;
    height: number;
    primaryColor: string;
    secondaryColor: string;
}

export type VisualizerRenderer = (context: VisualizerContext) => void;

export interface VisualizerPlugin {
    id: string;
    name: string;
    render: VisualizerRenderer;
}
