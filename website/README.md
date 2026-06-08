# UKBatch documentation website

The UKBatch documentation site, built with [Astro Starlight](https://starlight.astro.build).
It is published to GitHub Pages at `https://nspukcode-hub.github.io/UKBatch/` by the
`.github/workflows/website-deploy.yml` workflow whenever `website/**` changes on `main`.

This is a static site — not a .NET project. It is never built, packed, or published as part of
the NuGet release pipeline.

## Local development

```bash
cd website
npm install
npm run dev      # http://localhost:4321/UKBatch/
npm run build    # static output in website/dist
npm run preview  # serve the built site (exercises Pagefind search)
```

## Content

Pages live under `src/content/docs/` as Markdown / MDX. The navigation sidebar is defined in
`astro.config.mjs`. Most content is adapted from the repository's `README.md`,
`GETTING_STARTED.md`, `CHANGELOG.md`, and the per-package `README.md` files — keep them in sync
when those change.
