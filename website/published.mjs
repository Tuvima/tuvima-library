// Single source of truth for which docs/ pages are part of the published site.
// Engineering plans, proposals, reports, and design-system working files stay out.
export const publishedPatterns = [
  'index.md',
  '404.md',
  '{tutorials,guides,explanation,reference,architecture,product,install}/*.md',
  'design-system/{ui-consistency-standard,visual-qa-checklist}.md',
];
export const unpublishedPages = [
  'architecture/api-boundary-debt.md',
  'architecture/openapi-migration.md',
  'architecture/storage-policy-review.md',
  'architecture/account-profile-authentication-plan.md',
  'reference/sequence-dataset-gap-report-2026-07-15.md',
];

const toRegExp = (glob) =>
  new RegExp(
    '^' +
      glob
        .replace(/[.+^$()|[\]\\]/g, '\\$&')
        .replace(/\{([^}]+)\}/g, (_, alts) => `(?:${alts.split(',').join('|')})`)
        .replace(/\*/g, '[^/]*') +
      '$',
  );
const matchers = publishedPatterns.map(toRegExp);

export const isPublished = (relPath) =>
  matchers.some((re) => re.test(relPath)) && !unpublishedPages.includes(relPath);
