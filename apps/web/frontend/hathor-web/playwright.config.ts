import { defineConfig } from '@playwright/test'

// Visual/journey suite (plan §8): run against a live API + web dev server.
//   1. API:    dotnet run --project src/Hathor.Api (http://localhost:5041)
//   2. Web:    npm run dev -- --port 5173 (proxies /api to the API above)
//   3. Tests:  npx playwright test
// Screenshots land in e2e/screenshots for parity review vs Music Player/ui.
export default defineConfig({
  testDir: './e2e',
  fullyParallel: false,
  retries: 0,
  use: {
    baseURL: 'http://localhost:5173',
    screenshot: 'only-on-failure',
    trace: 'retain-on-failure',
  },
  webServer: undefined, // servers are started explicitly (see above)
})
