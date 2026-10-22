import { spawnSync } from "node:child_process";
import path from "node:path";
import { fileURLToPath } from "node:url";

const desktop = path.resolve(path.dirname(fileURLToPath(import.meta.url)), "..");
const extra = process.argv.slice(2);
const isLinux = process.platform === "linux";
const hasDisplay = Boolean(process.env.DISPLAY);

function run(command, args) {
  const result = spawnSync(command, args, { stdio: "inherit", cwd: desktop, env: process.env });
  if (result.error) throw result.error;
  process.exit(result.status ?? 1);
}

if (isLinux && !hasDisplay) {
  run("xvfb-run", ["-a", "-s", "-screen 0 1280x720x24", "pnpm", "exec", "playwright", "test", ...extra]);
} else {
  run("pnpm", ["exec", "playwright", "test", ...extra]);
}
