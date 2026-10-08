import { defineCollection } from 'astro:content';
import { glob } from 'astro/loaders';
import { docsSchema } from '@astrojs/starlight/schema';
import { z } from 'astro/zod';
import { publishedPatterns, unpublishedPages } from '../published.mjs';

// Pages live in the repo-level docs/ folder so GitHub browsing and existing
// references keep working. published.mjs decides which of them join the site.
export const collections = {
  docs: defineCollection({
    loader: glob({
      base: '../docs',
      pattern: [...publishedPatterns, ...unpublishedPages.map((p) => `!${p}`)],
      generateId: ({ entry }) => {
        const id = entry.replace(/\.(md|mdx)$/, '');
        return id === 'index' ? 'index' : id;
      },
    }),
    schema: docsSchema({
      extend: z.object({
        description: z.string().min(20),
        audience: z.enum(['user', 'administrator', 'developer', 'designer']),
        category: z.enum([
          'landing', 'tutorial', 'installation', 'guide',
          'explanation', 'reference', 'architecture', 'policy',
        ]),
        product_area: z.string().min(1),
        status: z.enum(['current', 'early-access', 'target-state']).default('current'),
        tags: z.array(z.string()).optional(),
      }),
    }),
  }),
};
