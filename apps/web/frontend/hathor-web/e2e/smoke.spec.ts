import { expect, test } from '@playwright/test'

// Auth + shell smoke: register a throwaway user, land on Home,
// screenshot the shell for visual parity review vs Music Player/ui.
test('register → home shell', async ({ page }) => {
  const user = `pw-${Date.now()}`
  await page.goto('/register')
  await expect(page.getByText('Hathor')).toBeVisible()
  await page.getByPlaceholder('Username').fill(user)
  await page.getByPlaceholder('Password').fill('playwright-pass-123')
  await page.getByRole('button', { name: 'Create account' }).click()
  await expect(page.getByRole('heading', { name: 'Home' })).toBeVisible({ timeout: 15000 })
  await page.screenshot({ path: 'e2e/screenshots/home.png' })
})

// Library journey: empty Songs view shows the empty state.
test('songs empty state', async ({ page }) => {
  const user = `pw-songs-${Date.now()}`
  await page.goto('/register')
  await page.getByPlaceholder('Username').fill(user)
  await page.getByPlaceholder('Password').fill('playwright-pass-123')
  await page.getByRole('button', { name: 'Create account' }).click()
  // Wait for registration to land (goto would abort the in-flight POST).
  await expect(page.getByRole('heading', { name: 'Home' })).toBeVisible({ timeout: 15000 })
  await page.goto('/songs')
  await expect(page.getByRole('heading', { name: 'All Songs' })).toBeVisible({ timeout: 15000 })
  await expect(page.getByText('No songs yet')).toBeVisible({ timeout: 15000 })
})
