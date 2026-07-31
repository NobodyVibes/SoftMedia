import { useEffect, useRef } from 'react';
import { HubConnectionBuilder, HubConnection, LogLevel, HubConnectionState } from '@microsoft/signalr';
import { useQueryClient } from '@tanstack/react-query';
import { useAuthStore } from '../store/authStore';

interface UseMediaHubOptions {
    libraryId?: string;
    mediaId?: string;
}

// Custom logger to filter out known SignalR errors during React Strict Mode unmounts
import type { ILogger } from '@microsoft/signalr';

class SignalRLogger implements ILogger {
    log(logLevel: LogLevel, message: string) {
        // Filter out "connection stopped during negotiation" errors
        if (message && (
            message.includes('The connection was stopped during negotiation') ||
            message.includes('Failed to start the connection')
        )) {
            return;
        }

        // Filter out logs below Warning level to keep console clean
        if (logLevel < LogLevel.Warning) {
            return;
        }

        // Forward other logs to console
        switch (logLevel) {
            case LogLevel.Critical:
            case LogLevel.Error:
                console.error(`[SignalR] ${message}`);
                break;
            case LogLevel.Warning:
                console.warn(`[SignalR] ${message}`);
                break;
            case LogLevel.Information:
                console.info(`[SignalR] ${message}`);
                break;
            case LogLevel.Debug:
            case LogLevel.Trace:
                console.debug(`[SignalR] ${message}`);
                break;
        }
    }
}

/**
 * React hook for real-time SignalR updates.
 * Automatically connects to the hub, joins appropriate groups, and invalidates queries on updates.
 */
export function useMediaHub({ libraryId, mediaId }: UseMediaHubOptions) {
    const queryClient = useQueryClient();
    // WS-6 T6.1: SignalR sends its token as ?access_token= on the WebSocket
    // handshake — a query string — so the hub must ride the MEDIA token; the
    // server rejects full access tokens there now. App.tsx gates the authed UI
    // until the media token exists, so this is normally true on mount.
    // Subscribed as a BOOLEAN: the media token rotates every ~15 min and a value
    // dependency would tear down a healthy hub connection on every rotation.
    const hasMediaToken = useAuthStore((s) => !!s.mediaToken);
    const connectionRef = useRef<HubConnection | null>(null);
    const optionsRef = useRef({ libraryId, mediaId });

    // Keep options ref updated — in an effect, not during render: a render
    // React later discards (StrictMode double-render, a thrown suspension)
    // must not have already leaked its values into the ref.
    useEffect(() => {
        optionsRef.current = { libraryId, mediaId };
    }, [libraryId, mediaId]);

    useEffect(() => {
        // Don't connect if not authenticated / media token not yet minted
        if (!hasMediaToken) return;

        // Build connection with auto-reconnect. The factory reads the store at
        // CALL time so automatic reconnects pick up a rotated media token
        // instead of replaying the one captured at mount.
        const connection = new HubConnectionBuilder()
            .withUrl('/hubs/media', {
                accessTokenFactory: () => useAuthStore.getState().mediaToken ?? ''
            })
            .withAutomaticReconnect([0, 2000, 5000, 10000, 30000]) // Retry: immediately, 2s, 5s, 10s, 30s
            .configureLogging(new SignalRLogger()) // Use custom logger
            .build();

        connectionRef.current = connection;
        let isMounted = true;

        // Handle incoming events
        connection.on('ItemAdded', (libId: string) => {
            console.debug('[SignalR] ItemAdded:', libId);
            queryClient.invalidateQueries({ queryKey: ['library', libId, 'items'] });
        });

        connection.on('ItemUpdated', (mediaId: string) => {
            console.debug('[SignalR] ItemUpdated:', mediaId);
            queryClient.invalidateQueries({ queryKey: ['media', mediaId] });
        });

        connection.on('ScanProgress', (libId: string, processed: number, total: number, status: string, stage: string) => {
            console.debug('[SignalR] ScanProgress:', libId, processed, total, status, stage);
            // Invalidate scan queue so live displays (TopBar bell entry, Settings
            // scan status cards) refetch. Server-side batching caps this at ~2/sec.
            queryClient.invalidateQueries({ queryKey: ['scanQueue'] });
        });

        connection.on('LibraryRecentUpdated', (libId: string) => {
            console.debug('[SignalR] LibraryRecentUpdated:', libId);
            // Invalidate the library recent query so that Home Page refreshes
            queryClient.invalidateQueries({ queryKey: ['libraryRecent', libId] });
            queryClient.invalidateQueries({ queryKey: ['recentMedia'] }); // Also invalidate global recent
        });

        // Handle reconnection - rejoin groups
        connection.onreconnected(() => {
            console.debug('[SignalR] Reconnected, rejoining groups...');
            const opts = optionsRef.current;
            if (opts.libraryId) connection.invoke('JoinLibrary', opts.libraryId).catch(console.error);
            if (opts.mediaId) connection.invoke('JoinMedia', opts.mediaId).catch(console.error);
        });

        // Start connection and join groups
        const startConnection = async () => {
            try {
                await connection.start();
                if (!isMounted) {
                    await connection.stop();
                    return;
                }

                console.debug('[SignalR] Connected');
                const opts = optionsRef.current;

                if (opts.libraryId) {
                    await connection.invoke('JoinLibrary', opts.libraryId).catch(err => console.error('[SignalR] Failed to join library group:', err));
                }
                if (opts.mediaId) {
                    await connection.invoke('JoinMedia', opts.mediaId).catch(err => console.error('[SignalR] Failed to join media group:', err));
                }
            } catch (err) {
                // Ignore AbortError which happens when unmounting during negotiation
                const errorMessage = err instanceof Error ? err.message : String(err ?? '');
                if (isMounted &&
                    !errorMessage.includes('The connection was stopped during negotiation') &&
                    !errorMessage.includes('Failed to start the connection')) {
                    console.error('[SignalR] Connection failed:', err);
                }
            }
        };

        startConnection();

        // Cleanup on unmount
        return () => {
            isMounted = false;
            // Only stop if we are connected or connecting
            if (connection.state !== HubConnectionState.Disconnected) {
                connection.stop()
                    .then(() => console.debug('[SignalR] Disconnected'))
                    .catch(() => {
                        // Ignore errors during stop
                    });
            }
        };
    }, [hasMediaToken, queryClient]);

    // Handle group changes without reconnecting
    useEffect(() => {
        const connection = connectionRef.current;
        if (!connection || connection.state !== HubConnectionState.Connected) return;

        // This effect handles when libraryId or mediaId changes after initial connection
        // For now, we just rejoin the new groups (leave is handled server-side on disconnect)
        if (libraryId) {
            connection.invoke('JoinLibrary', libraryId).catch(console.error);
        }
        if (mediaId) {
            connection.invoke('JoinMedia', mediaId).catch(console.error);
        }
    }, [libraryId, mediaId]);
}
