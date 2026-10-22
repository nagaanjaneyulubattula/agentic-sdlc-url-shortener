export function fmtMs(ms: number | null | undefined): string {
  if (ms === null || ms === undefined || Number.isNaN(ms)) return '-';
  if (ms < 1000) return `${Math.round(ms)} ms`;
  const s = ms / 1000;
  if (s < 60) return `${s.toFixed(s < 10 ? 2 : 1)} s`;
  const m = Math.floor(s / 60);
  const rem = Math.round(s - m * 60);
  return `${m}m ${rem}s`;
}

/** Accepts either a 0..1 fraction or a 0..100 percentage. */
export function fmtPct(v: number | null | undefined): string {
  if (v === null || v === undefined || Number.isNaN(v)) return '-';
  const pct = v <= 1 ? v * 100 : v;
  return `${pct.toFixed(pct % 1 === 0 ? 0 : 1)}%`;
}

export function fmtTime(iso: string | null | undefined): string {
  if (!iso) return '-';
  const d = new Date(iso);
  if (Number.isNaN(d.getTime())) return iso;
  return d.toLocaleTimeString([], { hour12: false }) + '.' + String(d.getMilliseconds()).padStart(3, '0');
}

export function fmtDateTime(iso: string | null | undefined): string {
  if (!iso) return '-';
  const d = new Date(iso);
  if (Number.isNaN(d.getTime())) return iso;
  return d.toLocaleString([], { hour12: false });
}

export function shortHash(h: string | null | undefined, n = 10): string {
  if (!h) return '-';
  return h.length > n ? h.slice(0, n) : h;
}

export function fmtBytes(b: number): string {
  if (b < 1024) return `${b} B`;
  if (b < 1024 * 1024) return `${(b / 1024).toFixed(1)} KB`;
  return `${(b / 1024 / 1024).toFixed(2)} MB`;
}

export function prettyJson(raw: string): string {
  try {
    let v: unknown = JSON.parse(raw);
    // tolerate a double-encoded payload (a JSON string that itself contains JSON)
    if (typeof v === 'string') {
      try {
        v = JSON.parse(v);
      } catch {
        /* keep the string */
      }
    }
    return typeof v === 'string' ? v : JSON.stringify(v, null, 2);
  } catch {
    return raw;
  }
}
