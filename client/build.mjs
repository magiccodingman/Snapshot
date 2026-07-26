import { copyFile, mkdir, readFile, writeFile } from "node:fs/promises";
import { dirname, resolve } from "node:path";
import { fileURLToPath } from "node:url";

const here = dirname(fileURLToPath(import.meta.url));
const packageJson = JSON.parse(await readFile(resolve(here, "package.json"), "utf8"));
const sourcePath = resolve(here, "src/snapshot-protocol.js");
const outputDirectory = resolve(here, "dist");
const source = (await readFile(sourcePath, "utf8"))
  .replaceAll("__SNAPSHOT_CLIENT_VERSION__", packageJson.version);

const compactedLines = [];
for (const [sourceLine, value] of source.split(/\r?\n/u).entries()) {
  const line = value.trim();
  if (line.length > 0 && !line.startsWith("//")) {
    compactedLines.push({ line, sourceLine });
  }
}
const minified = compactedLines.map(({ line }) => line).join("\n");

const base64 = "ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz0123456789+/";
function encodeVlq(value) {
  let encoded = "";
  let remaining = value < 0 ? ((-value) << 1) | 1 : value << 1;
  do {
    let digit = remaining & 31;
    remaining >>>= 5;
    if (remaining > 0) digit |= 32;
    encoded += base64[digit];
  } while (remaining > 0);
  return encoded;
}

let previousOriginalLine = 0;
const mappings = compactedLines.map(({ sourceLine }) => {
  const originalLineDelta = sourceLine - previousOriginalLine;
  previousOriginalLine = sourceLine;
  return `${encodeVlq(0)}${encodeVlq(0)}${encodeVlq(originalLineDelta)}${encodeVlq(0)}`;
}).join(";");

const sourceMap = {
  version: 3,
  file: "snapshot-protocol.min.js",
  sources: ["../src/snapshot-protocol.js"],
  sourcesContent: [source],
  names: [],
  mappings
};

await mkdir(outputDirectory, { recursive: true });
await copyFile(resolve(here, "../README.md"), resolve(here, "README.md"));
await copyFile(resolve(here, "../LICENSE"), resolve(here, "LICENSE"));
await copyFile(resolve(here, "../128x128_compressed.png"), resolve(here, "128x128_compressed.png"));
await writeFile(resolve(outputDirectory, "snapshot-protocol.js"), source, "utf8");
await writeFile(resolve(outputDirectory, "snapshot-protocol.min.js"), `${minified}\n//# sourceMappingURL=snapshot-protocol.min.js.map\n`, "utf8");
await writeFile(resolve(outputDirectory, "snapshot-protocol.min.js.map"), JSON.stringify(sourceMap), "utf8");

console.log(`Built Snapshot Protocol client ${packageJson.version}.`);
