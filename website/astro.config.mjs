import { defineConfig } from 'astro/config';
import starlight from '@astrojs/starlight';
import sidebarTopics from 'starlight-sidebar-topics';
import { unified } from '@astrojs/markdown-remark';
import fs from 'node:fs';
import rewriteLinks from './scripts/rewrite-links.mjs';
import { base, origin } from './scripts/content-contract.mjs';

const manifest = JSON.parse(fs.readFileSync(new URL('./publication.json', import.meta.url), 'utf8'));
const topics = ['Guides', 'Develop', 'Reference'].map((group) => {
  const pages = manifest.pages.filter(p => p.group === group && p.slug && p.slug !== '404');
  return {
    label: group,
    link: `/${pages[0]?.slug ?? ''}/`,
    icon: group === 'Guides' ? 'open-book' : group === 'Develop' ? 'puzzle' : 'list-format',
    items: [...new Set(pages.map(p => p.section))].map(section => ({
      label: section,
      collapsed: true,
      items: pages.filter(p => p.section === section).map(p => ({ slug: p.slug, ...(p.label ? { label: p.label } : {}) })),
    })),
  };
});
export default defineConfig({
  site: origin,
  base,
  trailingSlash: 'always',
  publicDir: './.generated/public',
  redirects: Object.fromEntries(Object.entries(manifest.redirects ?? {}).map(([from, to]) => [from, `${base}${to}`])),
  markdown: { processor: unified({ remarkPlugins: [rewriteLinks] }) },
  integrations: [starlight({
    title: 'Tuvima Library',
    description: 'Use, administer, and extend your local-first Tuvima Library.',
    logo: { light: './src/assets/tuvima-logo.svg', dark: './src/assets/tuvima-logo-dark.svg', replacesTitle: true, alt: 'Tuvima Library' },
    favicon: '/favicon.svg',
    social: [{ icon: 'github', label: 'GitHub', href: 'https://github.com/Tuvima/tuvima_library' }],
    editLink: { baseUrl: 'https://github.com/Tuvima/tuvima_library/edit/main/docs/' },
    lastUpdated: true,
    locales: { root: { label: 'English', lang: 'en' } },
    customCss: ['./src/styles/tuvima.css'],
    head: [{ tag: 'meta', attrs: { name: 'theme-color', content: '#0B1220' } }],
    components: { ThemeProvider: './src/components/ThemeProvider.astro', ThemeSelect: './src/components/ThemeSelect.astro' },
    plugins: [sidebarTopics(topics, { exclude: ['/', '/index', '/404'] })],
  })],
});
