/** @type {import('next').NextConfig} */
const nextConfig = {
  // Local-only tool: no telemetry, no image optimisation server, no remote anything.
  reactStrictMode: true,

  // Speed is the home page. The Now, History and Health pages were folded into
  // it or dropped, so their old addresses land there instead of on a 404.
  // Temporary, not permanent: a browser caches a 308 indefinitely, which would
  // pin these paths to /speed even if one of them came back.
  async redirects() {
    return ['/', '/history', '/health'].map((source) => ({
      source,
      destination: '/speed',
      permanent: false,
    }));
  },
};

export default nextConfig;
