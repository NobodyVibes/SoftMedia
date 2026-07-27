/** Server-side limit on Playlist.Name; a longer name is rejected with a 400. */
export const MAX_PLAYLIST_NAME_LENGTH = 120;

const COPY_SUFFIX = ' (copy)';

/**
 * Name for a copy of someone else's playlist.
 *
 * The suffix matters: the copy lands in "Your Playlists" while the original stays
 * under "Shared on this server", and two identically-named entries on the same
 * page are indistinguishable.
 *
 * A name at or near the limit has to lose its tail to make room — appending
 * blindly would push past the server's cap and fail the save outright, which is
 * a worse outcome than a slightly shortened name.
 */
export function copyPlaylistName(name: string): string {
    const trimmed = name.trim();
    if (trimmed.length + COPY_SUFFIX.length <= MAX_PLAYLIST_NAME_LENGTH) {
        return trimmed + COPY_SUFFIX;
    }
    const room = MAX_PLAYLIST_NAME_LENGTH - COPY_SUFFIX.length;
    return trimmed.slice(0, room).trimEnd() + COPY_SUFFIX;
}
