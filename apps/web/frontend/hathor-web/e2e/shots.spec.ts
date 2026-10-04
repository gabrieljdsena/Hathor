import { expect, test } from '@playwright/test'

const routes = [
  'login',
  'home',
  'songs',
  'download',
  'podcasts',
  'playlists',
  'history',
  'settings',
  'artists',
  'mix',
] as const

test('login screen', async ({ page }) => {
  await page.goto('/login')
  await expect(page.getByText('Hathor')).toBeVisible()
  await page.screenshot({ path: 'shots/shot-login.png' })
})

for (const route of routes.slice(1)) {
  test(`shot ${route}`, async ({ page }) => {
    const user = `shot-${route}-${Date.now()}`
    await page.goto('/register')
    await page.getByPlaceholder('Username').fill(user)
    await page.getByPlaceholder('Password').fill('shot-pass-123')
    await page.getByRole('button', { name: 'Create account' }).click()
    await expect(page.getByRole('heading', { name: 'Home' })).toBeVisible({ timeout: 15000 })
    if (route !== 'home') {
      await page.goto(`/${route}`)
      await page.waitForTimeout(1500)
    }
    await page.screenshot({ path: `shots/shot-${route}.png`, fullPage: false })
  })
}
