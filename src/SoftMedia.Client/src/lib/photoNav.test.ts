import { describe, expect, it } from 'vitest';
import { buildPhotoNavSuffix } from './photoNav';

describe('buildPhotoNavSuffix', () => {
    it('carries the slideshow flag — the regression that killed the slideshow after one advance', () => {
        expect(buildPhotoNavSuffix(null, true)).toBe('?slideshow=1');
    });

    it('carries album and slideshow together', () => {
        expect(buildPhotoNavSuffix('2024/Italy', true)).toBe('?album=2024%2FItaly&slideshow=1');
    });

    it('keeps the ROOT album ("" is a real album key, not absence)', () => {
        expect(buildPhotoNavSuffix('', false)).toBe('?album=');
    });

    it('is empty only when there is genuinely nothing to carry', () => {
        expect(buildPhotoNavSuffix(null, false)).toBe('');
    });

    it('carries the fullscreen flag alongside the others', () => {
        expect(buildPhotoNavSuffix(null, false, true)).toBe('?fs=1');
        expect(buildPhotoNavSuffix('Trip', true, true)).toBe('?album=Trip&slideshow=1&fs=1');
    });
});
