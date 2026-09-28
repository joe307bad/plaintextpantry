// The whole backend the screenshot run talks to, in one process and with no
// dependencies: the built client on /, the F# server's three endpoints under
// /api, and a PowerSync sync stream that hands the browser a pantry's worth of
// invented rows and then holds the connection open.
//
// Nothing is seeded and nothing persists. The app boots, asks who it is, is
// told, opens a sync stream, receives the fixtures as a single checkpoint, and
// from there behaves exactly as it does against the real stack.
//
//   PORT   port to listen on            (default 8080)
//   WEB    directory of the built app   (default ./web)
//   DEBUG  set to log every request     (default off)

import { createServer } from 'node:http';
import { readFile, stat } from 'node:fs/promises';
import { extname, join, normalize } from 'node:path';
import { rows, user } from './fixtures.js';

const PORT = Number(process.env.PORT ?? 8080);
const WEB = process.env.WEB ?? new URL('../web', import.meta.url).pathname;
const DEBUG = !!process.env.DEBUG;

const log = (...args) => DEBUG && console.log('[mock]', ...args);

// ---------------------------------------------------------------- sync data

/// One PowerSync bucket holding everything. The real service splits rows into
/// a bucket per sync-rules stream; the client does not care how many there
/// are, only that the checksums add up.
const BUCKET = 'e2e[]';

/// The fixtures as sync operations. PowerSync sums op checksums and compares
/// them to the bucket's, so zero everywhere adds up and validates.
const operations = rows().map((row, i) => ({
  op_id: String(i + 1),
  op: 'PUT',
  object_type: row.table,
  object_id: row.id,
  checksum: 0,
  subkey: `${row.table}/${row.id}`,
  data: JSON.stringify(row.data),
}));

const lastOpId = String(operations.length);

/// The NDJSON the client reads: a checkpoint promising N operations, the
/// operations, and the completion that makes them visible to the UI's queries.
const syncLines = () => [
  {
    checkpoint: {
      last_op_id: lastOpId,
      write_checkpoint: null,
      buckets: [{ bucket: BUCKET, checksum: 0, priority: 3, count: operations.length }],
    },
  },
  {
    data: {
      bucket: BUCKET,
      has_more: false,
      after: '0',
      next_after: lastOpId,
      data: operations,
    },
  },
  { checkpoint_complete: { last_op_id: lastOpId } },
];

/// A token shaped like the one the F# server mints. Nothing verifies the
/// signature here - the sync stream this is sent to is the mock below - but
/// the client does read it, so it has to parse as a JWT with a live `exp`.
const token = () => {
  const b64 = (o) => Buffer.from(JSON.stringify(o)).toString('base64url');
  const now = Math.floor(Date.now() / 1000);
  return [
    b64({ alg: 'HS256', typ: 'JWT', kid: 'e2e' }),
    b64({
      sub: user.id,
      iss: 'https://e2e.plaintextpantry.test',
      aud: 'powersync-dev',
      iat: now,
      exp: now + 60 * 60 * 24,
    }),
    'e2e-signature-not-verified',
  ].join('.');
};

// ------------------------------------------------------------ static assets

const MIME = {
  '.html': 'text/html; charset=utf-8',
  '.js': 'text/javascript; charset=utf-8',
  '.mjs': 'text/javascript; charset=utf-8',
  '.css': 'text/css; charset=utf-8',
  '.json': 'application/json; charset=utf-8',
  '.webmanifest': 'application/manifest+json; charset=utf-8',
  '.wasm': 'application/wasm',
  '.png': 'image/png',
  '.svg': 'image/svg+xml',
  '.ico': 'image/x-icon',
  '.ttf': 'font/ttf',
  '.woff2': 'font/woff2',
};

const sendFile = async (res, path) => {
  const body = await readFile(path);
  res.writeHead(200, {
    'content-type': MIME[extname(path)] ?? 'application/octet-stream',
    'cache-control': 'no-store',
  });
  res.end(body);
};

const serveStatic = async (res, pathname) => {
  const relative = normalize(decodeURIComponent(pathname)).replace(/^(\.\.[/\\])+/, '');
  const path = join(WEB, relative);
  try {
    if ((await stat(path)).isFile()) return await sendFile(res, path);
  } catch {
    /* fall through to the SPA shell */
  }
  // Every in-app route is index.html, the same fallback Caddy does.
  await sendFile(res, join(WEB, 'index.html'));
};

// ------------------------------------------------------------------- routes

const json = (res, status, body) => {
  const payload = JSON.stringify(body);
  res.writeHead(status, { 'content-type': 'application/json; charset=utf-8', 'cache-control': 'no-store' });
  res.end(payload);
};

const readBody = (req) =>
  new Promise((resolve) => {
    let body = '';
    req.on('data', (chunk) => (body += chunk));
    req.on('end', () => resolve(body));
  });

/// Holds the stream open after the data has gone out. PowerSync treats the end
/// of the response as the end of the connection and reconnects, which would
/// leave the app flickering between "syncing" and "synced" mid-screenshot.
const streamSync = (res) => {
  res.writeHead(200, {
    'content-type': 'application/x-ndjson',
    'cache-control': 'no-store',
    connection: 'keep-alive',
  });
  for (const line of syncLines()) res.write(JSON.stringify(line) + '\n');

  const keepAlive = setInterval(() => res.write(JSON.stringify({ token_expires_in: 3600 }) + '\n'), 20_000);
  res.on('close', () => clearInterval(keepAlive));
};

const server = createServer(async (req, res) => {
  const { pathname } = new URL(req.url, `http://${req.headers.host}`);
  log(req.method, pathname);

  try {
    switch (pathname) {
      // Signed in, always, as the one user this pantry belongs to.
      case '/api/auth/me':
        return json(res, 200, { id: user.id, email: user.email, name: user.name });

      // The sync endpoint is this same origin, so the stream below is what the
      // client connects to and there is no cross-origin request to allow.
      case '/api/sync/credentials':
        return json(res, 200, { endpoint: `http://${req.headers.host}`, token: token() });

      // Local writes are accepted and dropped. The screenshots never make one,
      // but a rejected upload would put the app into a retry loop.
      case '/api/sync/upload': {
        await readBody(req);
        return json(res, 200, {});
      }

      case '/sync/stream': {
        if (DEBUG) log('stream request', await readBody(req));
        return streamSync(res);
      }

      // Asked for after an upload, to learn which checkpoint contains it.
      case '/write-checkpoint2.json':
        return json(res, 200, { data: { write_checkpoint: lastOpId } });

      default:
        return await serveStatic(res, pathname);
    }
  } catch (err) {
    console.error('[mock]', err);
    if (!res.headersSent) json(res, 500, { error: String(err) });
    else res.end();
  }
});

server.listen(PORT, () => console.log(`[mock] serving ${WEB} and a pantry of ${operations.length} rows on :${PORT}`));
