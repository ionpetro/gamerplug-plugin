import { _electron as electron, expect, test, type ElectronApplication, type Page } from "@playwright/test";
import { execFileSync } from "node:child_process";
import fs from "node:fs";
import os from "node:os";
import path from "node:path";
import { createRequire } from "node:module";
import { fileURLToPath } from "node:url";

const require = createRequire(import.meta.url);
const electronBinary = require("electron") as string;
const desktopRoot = path.resolve(path.dirname(fileURLToPath(import.meta.url)), "..");

interface ProbeStream {
  codec_type?: string;
  codec_name?: string;
  duration?: string;
}

interface ProbeResult {
  streams?: ProbeStream[];
  format?: { duration?: string; format_name?: string };
}

function ffprobe(file: string): ProbeResult {
  const raw = execFileSync(
    "ffprobe",
    ["-v", "error", "-show_streams", "-show_format", "-print_format", "json", file],
    { encoding: "utf8" },
  );
  return JSON.parse(raw) as ProbeResult;
}

function durationOf(probe: ProbeResult): number {
  const fromFormat = Number(probe.format?.duration);
  if (Number.isFinite(fromFormat) && fromFormat > 0) return fromFormat;
  const fromStream = probe.streams?.map((s) => Number(s.duration)).find((n) => Number.isFinite(n) && n > 0);
  return fromStream ?? 0;
}

test("desktop clip save, trim, and stub upload", async () => {
  const userData = fs.mkdtempSync(path.join(os.tmpdir(), "gamerplug-e2e-"));
  const clipFolder = path.join(userData, "clips");
  fs.mkdirSync(clipFolder, { recursive: true });

  const consoleErrors: string[] = [];
  let app: ElectronApplication | undefined;
  let window: Page | undefined;

  try {
    app = await electron.launch({
      executablePath: electronBinary,
      args: [
        desktopRoot,
        `--user-data-dir=${userData}`,
        "--no-sandbox",
        "--disable-gpu",
        "--disable-dev-shm-usage",
      ],
      env: {
        ...process.env,
        GAMERPLUG_E2E: "1",
        GAMERPLUG_DEV_RECORDER: "1",
        GAMERPLUG_UPLOAD_MODE: "stub",
        GAMERPLUG_CLIP_FOLDER: clipFolder,
        GAMERPLUG_DEV_WIDTH: "640",
        GAMERPLUG_DEV_HEIGHT: "360",
        GAMERPLUG_DEV_FPS: "30",
        ELECTRON_DISABLE_SANDBOX: "1",
      },
      timeout: 60_000,
    });

    window = await app.firstWindow({ timeout: 60_000 });
    window.on("console", (msg) => {
      if (msg.type() === "error") consoleErrors.push(msg.text());
    });
    window.on("pageerror", (err) => consoleErrors.push(String(err)));

    await expect(window.locator("[data-testid='preload-missing']")).toHaveCount(0);
    await expect(window.locator("[data-testid='app']")).toBeVisible({ timeout: 45_000 });

    const state = window.locator("[data-testid='recorder-state']");
    await expect(state).toHaveText(/idle|capturing|starting/, { timeout: 45_000 });
    const current = ((await state.textContent()) ?? "").trim();
    if (current !== "capturing") {
      await window.locator("[data-testid='recorder-start']").click();
    }
    await expect(state).toHaveText("capturing", { timeout: 30_000 });

    // GOP is 2s; wait for at least one keyframe in the real ring buffer.
    await window.waitForTimeout(2800);
    await window.locator("[data-testid='clip-save']").click();

    const row = window.locator("[data-testid='clip-row']").first();
    await expect(row).toBeVisible({ timeout: 30_000 });
    const filePath = await row.getAttribute("data-filepath");
    expect(filePath).toBeTruthy();
    expect(fs.existsSync(filePath!)).toBe(true);

    const probe = ffprobe(filePath!);
    expect(probe.streams?.some((s) => s.codec_name === "h264")).toBe(true);
    const originalDuration = durationOf(probe);
    expect(originalDuration).toBeGreaterThan(0.4);
    expect(probe.format?.format_name ?? "").toMatch(/mp4/);

    await expect(window.locator("[data-testid='clip-video']")).toBeVisible();

    const trimEnd = Math.max(0.6, Math.min(originalDuration * 0.6, originalDuration - 0.1));
    await window.locator("[data-testid='trim-start']").fill("0");
    await window.locator("[data-testid='trim-end']").fill(trimEnd.toFixed(2));
    await window.locator("[data-testid='trim-apply']").click();

    await expect(async () => {
      const trimmedPath = await row.getAttribute("data-filepath");
      expect(trimmedPath).toBeTruthy();
      expect(trimmedPath).not.toBe(filePath);
      expect(fs.existsSync(trimmedPath!)).toBe(true);
      const trimmed = ffprobe(trimmedPath!);
      expect(trimmed.streams?.some((s) => s.codec_name === "h264")).toBe(true);
      const trimmedDur = durationOf(trimmed);
      expect(trimmedDur).toBeGreaterThan(0.2);
      expect(trimmedDur).toBeLessThan(originalDuration + 0.05);
    }).toPass({ timeout: 30_000 });

    await window.locator("[data-testid='upload-clip']").click();
    await expect(window.locator("[data-testid='clip-status']").first()).toHaveText(/published/, {
      timeout: 30_000,
    });
    await expect(row).toHaveAttribute("data-checksum-verified", "true");
    const crc = await row.getAttribute("data-crc32c");
    expect(crc && crc.length > 0).toBe(true);

    const lastUpload = JSON.parse(fs.readFileSync(path.join(userData, "last-upload.json"), "utf8")) as {
      checksumVerified: boolean;
      crc32c: string;
      status: string;
    };
    expect(lastUpload.status).toBe("published");
    expect(lastUpload.checksumVerified).toBe(true);
    expect(lastUpload.crc32c).toBe(crc);

    const ignored = [/Autofill\./i, /Electron Security Warning/i];
    const unexpected = consoleErrors.filter((e) => !ignored.some((re) => re.test(e)));
    expect(unexpected, `renderer console errors:\n${unexpected.join("\n")}`).toEqual([]);
  } finally {
    try {
      await app?.close();
    } catch {
      /* already gone */
    }
    fs.rmSync(userData, { recursive: true, force: true });
  }
});
