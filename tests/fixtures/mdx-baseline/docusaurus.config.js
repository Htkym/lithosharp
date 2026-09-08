module.exports = {
  title: 'LithoSharp parity fixture',
  url: 'https://example.test',
  baseUrl: '/product/',
  trailingSlash: true,
  onBrokenLinks: 'throw',
  i18n: { defaultLocale: 'en', locales: ['en', 'ja'] },
  markdown: { mdx1Compat: { comments: false, admonitions: false, headingIds: false } },
  presets: [['classic', { docs: { routeBasePath: 'guide' }, blog: false, theme: {} }]]
};
