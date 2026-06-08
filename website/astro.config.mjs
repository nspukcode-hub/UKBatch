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
				{
					label: 'Packages',
					items: [
						{ label: 'Overview', slug: 'packages' },
						{ label: 'UKBatch.Abstractions', slug: 'packages/abstractions' },
						{ label: 'UKBatch.Core', slug: 'packages/core' },
						{ label: 'UKBatch.AspNetCore', slug: 'packages/aspnetcore' },
						{ label: 'UKBatch.Worker', slug: 'packages/worker' },
						{ label: 'UKBatch.Api', slug: 'packages/api' },
						{ label: 'UKBatch.Dashboard', slug: 'packages/dashboard' },
						{
							label: 'UKBatch.Transport.Http',
							slug: 'packages/transport-http',
						},
						{
							label: 'UKBatch.Transport.RabbitMQ',
							slug: 'packages/transport-rabbitmq',
						},
						{
							label: 'UKBatch.Storage.EntityFrameworkCore',
							slug: 'packages/storage-efcore',
						},
					],
				},
				{ label: 'Changelog', slug: 'changelog' },
			],
		}),
	],
});
