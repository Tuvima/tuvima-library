import { defineCollection } from 'astro:content';
import { z } from 'astro/zod';
import { glob } from 'astro/loaders';
import { docsSchema } from '@astrojs/starlight/schema';

export const collections = {
  docs: defineCollection({
    loader: glob({ base: './.generated/content', pattern: '**/*.{md,mdx}', generateId: ({ data }) => String(data.slug || 'index') }),
    schema: docsSchema({ extend: z.object({
      description: z.string().trim().min(20),
      audience: z.enum(['user', 'administrator', 'developer', 'designer']),
      category: z.enum(['landing', 'tutorial', 'installation', 'guide', 'explanation', 'reference', 'architecture', 'policy']),
      product_area: z.string().trim().min(1),
      status: z.enum(['current', 'early-access', 'target-state']),
      tags: z.array(z.string()).optional(),
    }) }),
  }),
};
