import test from "node:test";
import assert from "node:assert/strict";
import { readFile } from "node:fs/promises";
import { resolve } from "node:path";

const sourcePath = resolve(import.meta.dirname, "../src/snapshot-protocol.js");

test("client uses the clean Snapshot Protocol contract", async () => {
  const source = await readFile(sourcePath, "utf8");
  assert.match(source, /snapshot:navigate/u);
  assert.match(source, /snapshot:result/u);
  assert.match(source, /snapshot:error/u);
  assert.match(source, /snapshot-ready/u);
  assert.doesNotMatch(source, /TruthSEO|TruthOrigin|truthseo|truthorigin/u);
});

test("client does not hard-code the legacy site version", async () => {
  const source = await readFile(sourcePath, "utf8");
  assert.doesNotMatch(source, /data-version[^\n]*["']9["']/u);
  assert.match(source, /data-site-version/u);
});

test("executor announces readiness before waiting for route content", async () => {
  const source = await readFile(sourcePath, "utf8");
  assert.doesNotMatch(source, /startupReadiness\s*=\s*await\s+waitForReadiness/u);
  const readyAssignment = source.indexOf("window.__snapshotProtocolReady = true");
  const clientReadyMessage = source.indexOf('type: "snapshot:client-ready"');
  assert.notEqual(readyAssignment, -1);
  assert.notEqual(clientReadyMessage, -1);
  assert.ok(readyAssignment < clientReadyMessage);
});