import { describe, it, expect } from 'vitest';
import { describeCastReadiness, isLoopbackHost, type CastReadinessInput } from './castReadiness';

const base: CastReadinessInput = {
    isSecureContext: true,
    hostname: 'media.example.com',
    isCastAvailable: true,
    castState: 'available',
    castUnavailableReason: null,
};

const check = (r: ReturnType<typeof describeCastReadiness>, label: string) =>
    r.checks.find((c) => c.label.startsWith(label))!;

describe('isLoopbackHost', () => {
    it('matches loopback forms and not LAN/domain', () => {
        for (const h of ['localhost', '127.0.0.1', '127.0.1.1', '0.0.0.0', '::1']) {
            expect(isLoopbackHost(h)).toBe(true);
        }
        for (const h of ['192.168.1.50', '10.0.0.4', 'media.example.com', 'media.local']) {
            expect(isLoopbackHost(h)).toBe(false);
        }
    });
});

describe('describeCastReadiness', () => {
    it('is ready when HTTPS, SDK available, and a device is present', () => {
        const r = describeCastReadiness(base);
        expect(r.ready).toBe(true);
        expect(check(r, 'Reachable HTTPS').status).toBe('ok');
        expect(check(r, 'Casting supported').status).toBe('ok');
        expect(check(r, 'A Google Cast device').status).toBe('ok');
    });

    it('flags an insecure (plain-HTTP LAN) origin and is not ready', () => {
        const r = describeCastReadiness({ ...base, isSecureContext: false, hostname: '192.168.1.50', isCastAvailable: false, castUnavailableReason: 'insecure-context' });
        expect(r.ready).toBe(false);
        expect(check(r, 'Reachable HTTPS').status).toBe('fail');
        expect(check(r, 'Casting supported').status).toBe('fail');
    });

    it('warns on localhost (button works but TV cannot reach it) and is not ready', () => {
        const r = describeCastReadiness({ ...base, hostname: 'localhost' });
        expect(r.ready).toBe(false);
        expect(check(r, 'Reachable HTTPS').status).toBe('warn');
        expect(check(r, 'Reachable HTTPS').detail).toMatch(/localhost/i);
    });

    it('explains the no-Cast-device case (LG/Samsung are not receivers) and is not ready', () => {
        const r = describeCastReadiness({ ...base, castState: 'no-devices' });
        expect(r.ready).toBe(false);
        const device = check(r, 'A Google Cast device');
        expect(device.status).toBe('fail');
        expect(device.detail).toMatch(/LG|Chromecast|Google TV/);
    });

    it('flags a browser without Cast support', () => {
        const r = describeCastReadiness({ ...base, isCastAvailable: false, castUnavailableReason: 'no-sdk' });
        expect(check(r, 'Casting supported').status).toBe('fail');
        expect(check(r, 'Casting supported').detail).toMatch(/Chrome|Edge/);
    });
});
