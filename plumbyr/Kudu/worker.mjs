// Read-only Plumbyr adapter around Kudu's original browser discovery code.
import { readFile, lstat, readdir } from 'node:fs/promises'
import { join } from 'node:path'

const VERSION = 1

async function measure(root) {
  let bytes = 0
  let files = 0
  let skipped = 0
  const pending = [root]
  while (pending.length) {
    const path = pending.pop()
    try {
      const info = await lstat(path)
      if (info.isSymbolicLink()) {
        skipped++
      } else if (info.isDirectory()) {
        for (const name of await readdir(path)) pending.push(join(path, name))
      } else if (info.isFile()) {
        bytes += info.size
        files++
      }
    } catch {
      skipped++
    }
  }
  return { bytes, files, skipped }
}

try {
  if (Number(process.versions.node.split('.')[0]) < 24) {
    throw new Error('Browser analysis requires Node.js 24 or newer. Install Node.js and restart Plumbyr.')
  }
  let input = ''
  for await (const chunk of process.stdin) {
    input += chunk
    if (input.length > 4096) throw new Error('Request is too large')
  }
  const request = JSON.parse(input)
  if (request.version !== VERSION || request.method !== 'analyze-browsers') {
    throw new Error('Unsupported browser analysis request')
  }

  const { chromiumBrowsers, chromiumCacheTargets } = await import('./vendor/chromium-cache.ts')
  const { buildCleanerPaths } = await import('./vendor/loader.ts')
  const browsers = JSON.parse(await readFile(new URL('./vendor/browsers.json', import.meta.url), 'utf8'))
  // Only browserPaths() is used; the other lazy rule accessors are intentionally not invoked.
  const paths = buildCleanerPaths({ browsers }, 'win32').browserPaths()
  const targets = []
  const warnings = []
  const seen = new Set()
  for (const browser of chromiumBrowsers(paths)) {
    try {
      await readdir(browser.base)
    } catch (error) {
      if (error.code !== 'ENOENT') warnings.push(`${browser.label}: browser profiles could not be read.`)
      continue
    }
    for (const target of await chromiumCacheTargets(browser)) {
      const key = target.path.toLowerCase()
      if (seen.has(key)) continue
      seen.add(key)
      targets.push({ browser: browser.label, ...target, ...await measure(target.path) })
    }
  }
  // These are logical bytes on disk, not a promise of safely reclaimable space.
  process.stdout.write(JSON.stringify({ version: VERSION, ok: true, targets, warnings }) + '\n')
} catch (error) {
  process.stdout.write(JSON.stringify({ version: VERSION, ok: false, error: String(error.message ?? error) }) + '\n')
  process.exitCode = 1
}
