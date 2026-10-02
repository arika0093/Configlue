# WebStorage browser contracts

Runs the production helper in Chromium using native Web Locks and browser storage.
Barriers pause SHA-256 calculation inside a lock; Playwright's controlled clock advances
past the former 30 second watchdog without waiting in real time. Closing a paused page
also verifies native lock release when its execution context disappears.

```sh
npm ci
npx playwright install chromium
npm test
```
