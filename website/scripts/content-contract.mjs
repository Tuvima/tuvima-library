import path from 'node:path';

export const base = '/tuvima_library';
export const origin = 'https://tuvima.github.io';
export const repoUrl = 'https://github.com/Tuvima/tuvima_library';
export const requiredEnums = {
  audience: ['user', 'administrator', 'developer', 'designer'],
  category: ['landing', 'tutorial', 'installation', 'guide', 'explanation', 'reference', 'architecture', 'policy'],
  status: ['current', 'early-access', 'target-state'],
};
export function normalizeSource(source) {
  const normalized = path.posix.normalize(source.replaceAll('\\', '/'));
  if (normalized.startsWith('../') || normalized.startsWith('/') || !/\.(md|mdx)$/.test(normalized)) {
    throw new Error(`Invalid publication source: ${source}`);
  }
  return normalized;
}
export function pageRoute(slug) {
  if (slug === '404') return `${base}/404.html`;
  return `${base}/${slug ? `${slug}/` : ''}`;
}
export function validateManifest(manifest) {
  if (!Array.isArray(manifest.pages) || !manifest.pages.length) throw new Error('publication.json must list pages');
  const sources = new Set();
  const slugs = new Set();
  for (const page of manifest.pages) {
    const source = normalizeSource(page.source);
    if (sources.has(source)) throw new Error(`Duplicate source: ${source}`);
    if (typeof page.slug !== 'string' || /(^\/|\/$|\.\.|[?#])/.test(page.slug)) throw new Error(`Invalid slug: ${page.slug}`);
    if (slugs.has(page.slug)) throw new Error(`Duplicate route: ${pageRoute(page.slug)}`);
    if (!['', '404'].includes(page.slug) && (!['Guides', 'Develop', 'Reference'].includes(page.group) || typeof page.section !== 'string' || !page.section.trim())) throw new Error(`Missing navigation placement: ${source}`);
    sources.add(source); slugs.add(page.slug);
  }
  for (const [from, to] of Object.entries(manifest.redirects ?? {})) {
    if (!/^\/(?:[\w-]+\/)+$/.test(from) || !to.startsWith('/') || to.startsWith(base)) throw new Error(`Invalid redirect: ${from} -> ${to}`);
    if (slugs.has(from.slice(1, -1))) throw new Error(`Redirect shadows page: ${from}`);
    if (!slugs.has(to.split('#')[0].replace(/^\/|\/$/g, ''))) throw new Error(`Redirect target is not a published page: ${to}`);
  }
}
export function validateMetadata(data, source) {
  for (const key of ['title', 'description', 'product_area']) {
    if (typeof data[key] !== 'string' || data[key].trim().length < (key === 'description' ? 20 : 1)) throw new Error(`${source}: required ${key} is missing or too short`);
  }
  for (const [key, values] of Object.entries(requiredEnums)) {
    if (!values.includes(data[key])) throw new Error(`${source}: ${key} must be one of ${values.join(', ')}`);
  }
}
