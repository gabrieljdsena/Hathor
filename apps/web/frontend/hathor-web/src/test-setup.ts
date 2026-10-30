import '@testing-library/jest-dom/vitest'

if (typeof HTMLMediaElement !== 'undefined') {
  Object.defineProperties(HTMLMediaElement.prototype, {
    play: {
      configurable: true,
      writable: true,
      value: () => Promise.resolve(),
    },
    pause: {
      configurable: true,
      writable: true,
      value: () => {},
    },
    load: {
      configurable: true,
      writable: true,
      value: () => {},
    },
  })
}
