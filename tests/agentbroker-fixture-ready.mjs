import fs from 'node:fs';

// Test-fixture boundary only. A ready name is published only after its synchronous write has closed.
export function publishFixtureReady(readyPath, value, {
  io = fs, now = () => performance.now(), later = setTimeout,
  fail = code => { try { fs.writeSync(2, 'FIXTURE_READY_PUBLISH_FAILED ' + code + '\n'); } catch {} process.exit(3); },
} = {}) {
  const pendingReadyPath = readyPath + '.pending';
  io.writeFileSync(pendingReadyPath, JSON.stringify(value));
  const retryCodes = new Set(['EPERM', 'EACCES', 'EBUSY']);
  const retryUntil = now() + 1000;
  let retries = 0;
  const publishReady = () => {
    try { io.renameSync(pendingReadyPath, readyPath); }
    catch (error) {
      const code = retryCodes.has(error?.code) ? error.code : 'OTHER';
      if (code !== 'OTHER' && retries < 20 && now() < retryUntil) {
        retries++;
        later(publishReady, 25);
        return;
      }
      // Never export an underlying error's path, message or stack.
      fail(code);
    }
  };
  publishReady();
}
