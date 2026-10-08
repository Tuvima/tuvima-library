// @ts-check
import { defineConfig } from 'astro/config';
import starlight from '@astrojs/starlight';
import starlightSidebarTopics from 'starlight-sidebar-topics';
import starlightImageZoom from 'starlight-image-zoom';
import { unified } from '@astrojs/markdown-remark';
import rehypeDocsLinks from './rehype-docs-links.mjs';
import { redirects } from './redirects.mjs';

const repo = 'https://github.com/Tuvima/tuvima_library';

export default defineConfig({
  site: 'https://tuvima.github.io',
  base: '/tuvima_library',
  trailingSlash: 'always',
  redirects,
  markdown: {
    processor: unified({
      rehypePlugins: [
        // Pages keep plain relative .md links so they stay clickable on GitHub.
        [rehypeDocsLinks, { base: '/tuvima_library' }],
      ],
    }),
  },
  integrations: [
    starlight({
      title: 'Tuvima Library',
      description: 'Documentation for Tuvima Library, the private, local-first story library.',
      logo: {
        light: './src/assets/tuvima-logo.svg',
        dark: './src/assets/tuvima-logo-dark.svg',
        alt: 'Tuvima Library',
        replacesTitle: true,
      },
      favicon: '/favicon.svg',
      social: [{ icon: 'github', label: 'GitHub', href: repo }],
      editLink: { baseUrl: `${repo}/edit/main/docs/` },
      lastUpdated: true,
      customCss: ['./src/styles/tuvima.css'],
      head: [
        { tag: 'meta', attrs: { name: 'theme-color', content: '#0B1220' } },
        // Dark by default; the toggle still works and remembers the reader's choice.
        {
          tag: 'script',
          content:
            "try{if(!localStorage.getItem('starlight-theme'))localStorage.setItem('starlight-theme','dark')}catch(e){}",
        },
      ],
      plugins: [
        starlightImageZoom(),
        starlightSidebarTopics([
          {
            label: 'Guides',
            link: '/tutorials/getting-started/',
            icon: 'open-book',
            items: [
              {
                label: 'Start here',
                items: [
                  'tutorials/getting-started',
                  'tutorials/first-library',
                  'guides/troubleshooting',
                ],
              },
              {
                label: 'Install',
                items: [
                  'install/docker',
                  'install/windows',
                  'install/unraid',
                  'install/synology',
                  'install/qnap',
                  'install/truenas-scale',
                ],
              },
              {
                label: 'Use Tuvima',
                items: [
                  { slug: 'explanation/how-universes-work', label: 'Collections, Universes, and Shelves' },
                  'guides/resolving-reviews',
                  { slug: 'guides/account-security', label: 'Profiles, Accounts, and Recovery' },
                  'guides/using-plugins',
                ],
              },
              {
                label: 'Set up and manage',
                items: [
                  'guides/library-settings',
                  'guides/adding-media',
                  'guides/configuring-providers',
                  'guides/language-setup',
                  { slug: 'guides/local-ai-model-rollout', label: 'Local AI Models' },
                  'guides/remote-access',
                  'guides/external-authentication',
                  'guides/operations-and-recovery',
                ],
              },
              {
                label: 'How it works',
                items: [
                  'explanation/how-the-pipeline-works',
                  'explanation/how-ingestion-works',
                  'explanation/how-hydration-works',
                  'explanation/how-scoring-works',
                  'explanation/how-ai-works',
                  'explanation/privacy-local-first',
                ],
              },
            ],
          },
          {
            label: 'Develop',
            link: '/tutorials/dev-setup/',
            icon: 'puzzle',
            items: [
              {
                label: 'Contribute',
                items: [
                  'tutorials/dev-setup',
                  'guides/running-tests',
                  'guides/repository-storage',
                  'guides/shared-ai-storage',
                ],
              },
              {
                label: 'Extend Tuvima',
                items: [
                  'guides/building-a-plugin',
                  'reference/plugin-catalog',
                  'guides/adding-a-provider',
                  'architecture/provider-onboarding',
                  'guides/writing-a-processor',
                ],
              },
              {
                label: 'Architecture',
                items: [
                  'architecture/technical-overview',
                  'architecture/architecture-summary',
                  'architecture/project-boundaries',
                  'architecture/api-boundaries',
                  'architecture/configuration',
                  'architecture/library-model-and-intake',
                  'architecture/ingestion-pipeline',
                  'architecture/ingestion-identity-enrichment-pipeline',
                  'architecture/durable-media-operations',
                  'architecture/hydration-and-providers',
                  'architecture/provider-enrichment-strategy',
                  'architecture/scoring-and-cascade',
                  'architecture/collections',
                  'architecture/universe-graph',
                  'architecture/artwork',
                  'architecture/view-personal-media',
                  'architecture/dashboard-ui',
                  'architecture/playback',
                  'architecture/inline-media-editing',
                  'architecture/js-interop',
                  'architecture/settings',
                  'architecture/localization',
                  'architecture/security',
                  'architecture/network-and-remote-access',
                  'architecture/ai-integration',
                  'architecture/local-ai-model-portfolio',
                  'architecture/performance-and-large-libraries',
                ],
              },
              {
                label: 'Decisions and future state',
                items: [
                  'architecture/storage-policy-adr',
                  'architecture/target-state',
                  'architecture/display-api',
                ],
              },
              {
                label: 'Design system',
                items: [
                  'design-system/ui-consistency-standard',
                  'design-system/visual-qa-checklist',
                ],
              },
            ],
          },
          {
            label: 'Reference',
            link: '/reference/configuration/',
            icon: 'list-format',
            items: [
              {
                label: 'Configuration',
                items: ['reference/configuration', 'reference/ai-configuration', 'reference/providers'],
              },
              {
                label: 'Media',
                items: [
                  'reference/media-types',
                  'reference/artwork-types',
                  'reference/secondary-title-text',
                  'reference/wikidata-property-map',
                ],
              },
              {
                label: 'Engine',
                items: ['reference/api-endpoints', 'reference/display-api', 'reference/database-schema'],
              },
              {
                label: 'Project',
                items: [
                  'product/status',
                  'product/beta-roadmap',
                  'product/feature-truth-inventory',
                  'product/presentation-rules',
                  'product/native-clients',
                  'product/security',
                  'reference/attributions',
                  'reference/glossary',
                ],
              },
            ],
          },
        ], { exclude: ['/404'] }),
      ],
    }),
  ],
});
