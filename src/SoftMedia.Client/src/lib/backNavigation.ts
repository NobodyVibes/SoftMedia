import { MediaType, type Library, type MediaItem } from '../types';

/**
 * Back-navigation targets are HIERARCHICAL, never browser history (`navigate(-1)`).
 * Players, the reader, and detail pages are all deep-linked from home rows, search,
 * libraries, collections and each other, so history-back is entry-dependent — and can
 * loop (player back → detail page → history-back → player again). Every back control
 * resolves its destination through this module so the rules live in exactly one place.
 */

/**
 * Where a player/reader "Back" lands: the media's detail surface. For an episode that
 * is its SERIES page — the app has no per-episode detail pages (every launch path,
 * from library quick-play to the series page's episode list, passes through the
 * series detail surface).
 */
export function playerBackTarget(item: Pick<MediaItem, 'id' | 'seriesId'>): string {
    return `/media/${item.seriesId ?? item.id}`;
}

/**
 * Where a detail page's "Back" lands: one level up the containment chain.
 * - photo → its album in the photo library (the album key rides on the photo URL;
 *   PhotoLibraryView reads the same ?album= param) — after paging through 20 photos,
 *   history-back would replay all 20
 * - episode → its series page; track → its album; album → its artist
 * - top-level items (movies, series, artists, books…) → their library
 */
export function detailBackTarget(
    item: Pick<MediaItem, 'id'> & Partial<Pick<MediaItem, 'type' | 'seriesId' | 'albumId' | 'artistId' | 'libraryId'>>,
    photoAlbumKey?: string | null,
): string {
    if (item.type === MediaType.Photo && item.libraryId) {
        return `/libraries/${item.libraryId}${photoAlbumKey != null ? `?album=${encodeURIComponent(photoAlbumKey)}` : ''}`;
    }
    if (item.seriesId) return `/media/${item.seriesId}`;
    if (item.albumId) return `/media/${item.albumId}`;
    if (item.artistId && item.type === MediaType.Album) return `/media/${item.artistId}`;
    if (item.libraryId) return `/libraries/${item.libraryId}`;
    return '/';
}

/** Query param LibraryPage reads to open a non-default view-mode tab. */
export const LIBRARY_VIEW_PARAM = 'view';

/**
 * Query param carrying the library a playlist was opened from. Playlists are not
 * owned by a library, so containment can't be derived from the playlist itself —
 * the origin rides on the URL instead, the same way a photo's album key does (see
 * detailBackTarget). That keeps back deep-linkable and refresh-proof without
 * touching browser history.
 */
export const PLAYLIST_ORIGIN_PARAM = 'from';

/** URL of a Music library's Playlists tab. */
function playlistsTabHref(libraryId: string): string {
    return `/libraries/${libraryId}?${LIBRARY_VIEW_PARAM}=playlists`;
}

/**
 * Where a playlist detail page's "All playlists" lands.
 *
 * There is NO `/playlists` route: the index is a view-mode tab inside the Music
 * library, because playlists are music-only in v1 (see PlaylistsView). Linking
 * to `/playlists` therefore matched nothing and App's catch-all `<Navigate to="/">`
 * bounced the user to the home page. Resolve the Music library and deep-link its
 * tab instead.
 *
 * With several Music libraries, `originLibraryId` (from PLAYLIST_ORIGIN_PARAM)
 * returns the user to the one they actually came from; it's validated against the
 * library list so a stale or hand-edited id can't strand them on a dead library
 * page. Falls back to the first Music library, then to home — the latter only
 * when no Music library exists, in which case there is nowhere else to go.
 */
export function playlistsIndexTarget(
    libraries: readonly Pick<Library, 'id' | 'type'>[] | undefined,
    originLibraryId?: string | null,
): string {
    const music = libraries?.filter(l => l.type === 'Music');

    if (originLibraryId) {
        // While the library list is still loading there is nothing to validate
        // against, and the id came from our own link — trust it so the href
        // doesn't flicker to a fallback and back.
        if (!music || music.some(l => l.id === originLibraryId)) {
            return playlistsTabHref(originLibraryId);
        }
    }

    return music?.[0] ? playlistsTabHref(music[0].id) : '/';
}
