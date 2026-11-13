import { existsSync, readFileSync } from 'node:fs';
import { resolve } from 'node:path';
import { describe, expect, it } from 'vitest';

describe('PWA foundation', () => {
  it('uses the React-only launch and scope routes and references existing icons', () => {
    const root = resolve(process.cwd(), 'public');
    const manifest = JSON.parse(readFileSync(resolve(root, 'manifest.webmanifest'), 'utf8')) as {
      name: string;
      short_name: string;
      start_url: string;
      scope: string;
      display: string;
      icons: Array<{ src: string; sizes: string }>;
    };

    expect(manifest.name).toBe('Saturday Scout');
    expect(manifest.short_name).toBe('Scout');
    expect(manifest.start_url).toBe('/react/');
    expect(manifest.scope).toBe('/react/');
    expect(manifest.display).toBe('standalone');
    for (const icon of manifest.icons) {
      expect(existsSync(resolve(root, icon.src.replace('/react/', '')))).toBe(true);
    }
    expect(existsSync(resolve(root, 'icons/apple-touch-icon.png'))).toBe(true);
  });
});
