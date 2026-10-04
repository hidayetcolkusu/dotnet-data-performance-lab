// Shared setup for the load scripts: read the environment, load the fixture and fail loudly
// before any traffic is generated. A run that starts with a bad fixture or a wrong base URL
// would still produce a summary file, and that file would look like a measurement.

import { SharedArray } from 'k6/data';

export const FIXTURE_SCHEMA = 'dpl.load-fixture.v1';
export const SUMMARY_SCHEMA = 'dpl.load-summary.v1';

const ARMS = ['cache-off', 'cache-on'];

function required(name) {
  const value = __ENV[name];
  if (value === undefined || value === null || value.trim() === '') {
    throw new Error(`${name} is required. Pass it with -e ${name}=...`);
  }
  return value.trim();
}

function optional(name, fallback) {
  const value = __ENV[name];
  return value === undefined || value === null || value.trim() === '' ? fallback : value.trim();
}

function positiveInt(name, fallback) {
  const raw = optional(name, String(fallback));
  const value = Number(raw);
  if (!Number.isInteger(value) || value < 1) {
    throw new Error(`${name} must be a positive integer, was '${raw}'.`);
  }
  return value;
}

export function baseUrl() {
  const url = required('BASE_URL').replace(/\/+$/, '');
  if (!/^https?:\/\//.test(url)) {
    throw new Error(`BASE_URL must start with http:// or https://, was '${url}'.`);
  }
  return url;
}

export function arm() {
  const value = optional('ARM', 'cache-off');
  if (!ARMS.includes(value)) {
    throw new Error(`ARM must be one of ${ARMS.join(', ')}, was '${value}'.`);
  }
  return value;
}

export const config = {
  rate: () => positiveInt('RATE', 20),
  duration: () => optional('DURATION', '60s'),
  warmupDuration: () => optional('WARMUP_DURATION', '30s'),
  repetition: () => positiveInt('REPETITION', 1),
  order: () => optional('ORDER', 'AB'),
  summaryOut: () => optional('SUMMARY_OUT', ''),
};

// Parsed once per process and shared across VUs: a per-VU copy of 1,000 rows would multiply
// the memory the load generator itself needs, which is exactly the wrong thing to spend on.
function loadFixture() {
  const path = required('FIXTURE');
  const parsed = JSON.parse(open(path));

  if (parsed.schema !== FIXTURE_SCHEMA) {
    throw new Error(`${path} has schema '${parsed.schema}', expected '${FIXTURE_SCHEMA}'.`);
  }
  if (!Array.isArray(parsed.products) || parsed.products.length === 0) {
    throw new Error(`${path} contains no products.`);
  }
  if (parsed.products.length !== parsed.count) {
    throw new Error(
      `${path} declares count ${parsed.count} but carries ${parsed.products.length} products.`,
    );
  }

  for (const p of parsed.products) {
    if (typeof p.sku !== 'string' || p.sku === '') {
      throw new Error(`${path} contains a product with no SKU.`);
    }
    if (typeof p.id !== 'number' || typeof p.categoryId !== 'number' || typeof p.unitPrice !== 'number') {
      throw new Error(`${path}: product ${p.sku} is missing the expected detail identity fields.`);
    }
  }

  return parsed;
}

export const fixture = new SharedArray('fixture', () => [loadFixture()])[0];

// Deterministic, collision-free spread over the fixture: the global iteration index means
// VU 1 and VU 40 never both start on the first SKU, and a repeated run visits the SKUs in
// the same order. Under constant-arrival-rate this cycles the whole fixture, not a hot head.
export function pickProduct(iteration) {
  return fixture.products[iteration % fixture.products.length];
}

/** Matches the API's two-decimal money contract without depending on float equality. */
export function priceMatches(actual, expected) {
  return typeof actual === 'number' && Math.abs(actual - expected) < 0.005;
}

export function buildSummary(data, extra) {
  const checks = data.metrics.checks || {};
  const httpReqFailed = data.metrics.http_req_failed || {};
  const dropped = data.metrics.dropped_iterations || {};
  const iterations = data.metrics.iterations || {};
  const httpReqs = data.metrics.http_reqs || {};
  const latency = (data.metrics.detail_latency_ms || {}).values || {};

  const checksTotal = (checks.values || {}).passes !== undefined
    ? (checks.values.passes || 0) + (checks.values.fails || 0)
    : 0;

  return {
    schema: SUMMARY_SCHEMA,
    arm: arm(),
    repetition: config.repetition(),
    order: config.order(),
    baseUrl: baseUrl(),
    fixtureHash: fixture.fixtureHash,
    fixtureSkuCount: fixture.products.length,
    rate: config.rate(),
    duration: config.duration(),
    metrics: {
      iterations: (iterations.values || {}).count || 0,
      httpReqs: (httpReqs.values || {}).count || 0,
      // http_req_failed is a Rate: passes are the *failed* requests.
      httpReqFailed: (httpReqFailed.values || {}).passes || 0,
      checksTotal,
      checksFailed: (checks.values || {}).fails || 0,
      droppedIterations: (dropped.values || {}).count || 0,
      detailLatencyMs: {
        avgMs: latency.avg || 0,
        medMs: latency.med || 0,
        p95Ms: latency['p(95)'] || 0,
        p99Ms: latency['p(99)'] || 0,
        minMs: latency.min || 0,
        maxMs: latency.max || 0,
        count: latency.count || 0,
      },
    },
    ...extra,
  };
}
