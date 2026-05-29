import { useEffect, useRef, useState, useCallback } from 'react';

interface TrickplayManifest {
    version: number;
    interval: number;
    tileWidth: number;
    tileHeight: number;
    columns: number;
    rows: number;
    tilesPerSheet: number;
    sheets: string[];
}

export interface SpriteFrame {
    sheetUrl: string;
    /** CSS background-position offsets (px) into the sheet for this tile. */
    x: number;
    y: number;
    tileWidth: number;
    tileHeight: number;
    /** Full sheet pixel dimensions, for background-size. */
    sheetWidth: number;
    sheetHeight: number;
}

/**
 * Loads a media item's trickplay manifest (P2-WI-001) and maps a playback time to the
 * correct sprite tile. Returns `null` from `frameAt` when no trickplay exists, so the
 * caller can fall back to the on-demand frame endpoint.
 */
export function useTrickplay(itemId: string, token: string | null) {
    const [manifest, setManifest] = useState<TrickplayManifest | null>(null);
    const triedRef = useRef(false);

    useEffect(() => {
        triedRef.current = false;
        setManifest(null);
        if (!itemId || !token) return;

        let cancelled = false;
        (async () => {
            try {
                const res = await fetch(`/api/v1/trickplay/${itemId}/manifest.json?token=${token}`);
                if (cancelled) return;
                triedRef.current = true;
                if (res.ok) setManifest(await res.json());
            } catch {
                triedRef.current = true;
            }
        })();
        return () => { cancelled = true; };
    }, [itemId, token]);

    const frameAt = useCallback((time: number): SpriteFrame | null => {
        if (!manifest || manifest.sheets.length === 0) return null;

        const index = Math.floor(time / manifest.interval);
        const sheetIdx = Math.floor(index / manifest.tilesPerSheet);
        if (sheetIdx >= manifest.sheets.length) return null;

        const within = index % manifest.tilesPerSheet;
        const col = within % manifest.columns;
        const row = Math.floor(within / manifest.columns);

        return {
            sheetUrl: `/api/v1/trickplay/${itemId}/${manifest.sheets[sheetIdx]}?token=${token}`,
            x: col * manifest.tileWidth,
            y: row * manifest.tileHeight,
            tileWidth: manifest.tileWidth,
            tileHeight: manifest.tileHeight,
            sheetWidth: manifest.columns * manifest.tileWidth,
            sheetHeight: manifest.rows * manifest.tileHeight,
        };
    }, [manifest, itemId, token]);

    return { hasTrickplay: manifest !== null, frameAt };
}
