// @ts-check
import { defineConfig } from 'astro/config';
import starlight from '@astrojs/starlight';

// https://astro.build/config
export default defineConfig({
	// GitHub Pages project site: served from https://nspukcode-hub.github.io/UKBatch/.
	// `base` keeps every generated URL under that sub-path, in dev and in production alike.
	site: 'https://nspukcode-hub.github.io',
	base: '/UKBatch',
	integrations: [
		starlight({
			title: 'UKBatch',
			description:
				'Lite, pluggable batch and job orchestration for .NET 8 and .NET 10 microservices.',
			logo: { src: './src/assets/logo.png', alt: 'UKBatch' },
			favicon: '/icon.png',
			customCss: ['./src/styles/custom.css'],
			social: [
				{
					icon: 'github',
					label: 'GitHub',
					href: 'https://github.com/nspukcode-hub/UKBatch',
				},
			],
			sidebar: [
				{
					label: 'Start here',
					items: [
						{ label: 'Introduction', slug: 'getting-started' },
						{ label: 'Add the dashboard', slug: 'getting-started/dashboard' },
						{ label: 'Persistent storage', slug: 'getting-started/storage' },
						{
							label: 'Cross-service workflows',
							slug: 'getting-started/cross-service',
						},
						{
							label: 'Server + workers',
							slug: 'getting-started/server-workers',
						},
					],
				},
				{
					label: 'Concepts',
					items: [
						{ label: 'Deployment modes', slug: 'concepts/deployment-modes' },
						{
							label: 'Workflow building blocks',
							slug: 'concepts/workflow-building-blocks',
						},
						{ label: 'Gotchas', slug: 'concepts/gotchas' },
					],
				},
				{ label: 'Changelog', slug: 'changelog' },
			],
		}),
	],
});
