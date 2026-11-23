// Contract-test formats. Email is an ASCII mailbox (no display name or comments);
// URI must be absolute. Keep format validation enabled under strict AJV.
export function addContractFormats(ajv) {
  ajv.addFormat("email", value => {
    if (value.length > 254) return false;
    const parts = value.split("@");
    if (parts.length !== 2) return false;
    const [local, domain] = parts;
    return local.length <= 64 && /^[a-z0-9!#$%&'*+/=?^_`{|}~-]+(?:\.[a-z0-9!#$%&'*+/=?^_`{|}~-]+)*$/i.test(local)
      && domain.length <= 253 && domain.split(".").every(label =>
        label.length <= 63 && /^[a-z0-9](?:[a-z0-9-]*[a-z0-9])?$/i.test(label));
  });
  ajv.addFormat("uri", value => {
    if (/[\s\u0000-\u001f\u007f]/u.test(value) || /%(?![0-9a-f]{2})/i.test(value)) return false;
    try { return Boolean(new URL(value).protocol); } catch { return false; }
  });
  ajv.addFormat("date-time", value => {
    // App timestamps are UTC, with up to PostgreSQL's microsecond precision.
    if (!/^\d{4}-\d{2}-\d{2}T\d{2}:\d{2}:\d{2}(?:\.\d{1,6})?Z$/.test(value)) return false;
    const parsed = new Date(value);
    return Number.isFinite(parsed.getTime()) && parsed.toISOString().slice(0, 19) === value.slice(0, 19);
  });
  return ajv;
}
