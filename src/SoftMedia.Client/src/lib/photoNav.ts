/**
 * Builds the query suffix that photo-viewer navigation must carry between photos:
 * the album scope (may legitimately be "" — the root album) and the slideshow flag.
 *
 * Implementation note: uses URLSearchParams.toString(), deliberately NOT `.size` —
 * `.size` is an ES2023 addition some engines lack, and `undefined > 0` being false
 * once silently dropped both params on every navigation (slideshow died after one
 * advance).
 */
export function buildPhotoNavSuffix(albumKey: string | null, slideshow: boolean): string {
    const params = new URLSearchParams();
    if (albumKey !== null) params.set('album', albumKey);
    if (slideshow) params.set('slideshow', '1');
    const query = params.toString();
    return query !== '' ? `?${query}` : '';
}
