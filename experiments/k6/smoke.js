// Correctness smoke, not a benchmark.
//
// Walks a slice of the fixture once and asserts that every detail response carries the row
// the fixture says it should. There is no latency threshold here on purpose: this is the
// script CI runs, and CI hardware makes no promise about timing.
//
// Exit code 0 means the API served the fixture correctly. A fixture holding a SKU the
// catalog does not contain produces a 404, fails the check threshold, and exits nonzero.

import http from 'k6/http';
import { check } from 'k6';
import { scenario } from 'k6/execution';
import { baseUrl, config, fixture, pickProduct, priceMatches } from './lib/fixture.js';

const BASE_URL = baseUrl();
const SUMMARY_OUT = config.summaryOut();

// Enough SKUs to exercise a spread of the catalog, few enough to stay a few seconds in CI.
const ITERATIONS = Math.min(fixture.products.length, Number(__ENV.SMOKE_ITERATIONS || 100));

export const options = {
  scenarios: {
    smoke: {
      executor: 'shared-iterations',
      vus: 5,
      iterations: ITERATIONS,
      maxDuration: '60s',
    },
  },
  thresholds: {
    checks: ['rate==1'],
    http_req_failed: ['rate==0'],
    // A truncated smoke would pass every check it managed to run; require the full walk.
    iterations: [`count>=${ITERATIONS}`],
  },
};

export function setup() {
  // Fail before generating traffic if the API is not the one we think we are testing.
  const ready = http.get(`${BASE_URL}/health/ready`, { headers: { Accept: 'application/json' } });
  if (ready.status !== 200) {
    throw new Error(
      `${BASE_URL}/health/ready returned ${ready.status}; the API is not ready to be smoke tested.`,
    );
  }
  return { iterations: ITERATIONS, fixtureHash: fixture.fixtureHash };
}

export default function () {
  // Evenly strided across the fixture rather than the first N, so the smoke covers the
  // whole catalog range the fixture was built from.
  const stride = Math.max(1, Math.floor(fixture.products.length / ITERATIONS));
  const expected = pickProduct(scenario.iterationInTest * stride);

  const response = http.get(`${BASE_URL}/api/products/${expected.sku}`, {
    tags: { name: 'product-detail-smoke' },
    headers: { Accept: 'application/json' },
  });

  let body = null;
  if (response.status === 200) {
    try {
      body = response.json();
    } catch (err) {
      body = null;
    }
  }

  check(response, {
    'status is 200': (r) => r.status === 200,
    'body parses': () => body !== null,
    'sku matches the fixture': () => body !== null && body.sku === expected.sku,
    'id matches the fixture': () => body !== null && body.id === expected.id,
    'name matches the fixture': () => body !== null && body.name === expected.name,
    'categoryId matches the fixture': () => body !== null && body.categoryId === expected.categoryId,
    'unitPrice matches the fixture': () => body !== null && priceMatches(body.unitPrice, expected.unitPrice),
    'isActive matches the fixture': () => body !== null && body.isActive === expected.isActive,
    'version is present': () => body !== null && typeof body.version === 'string' && body.version.length > 0,
  });
}

export function handleSummary(data) {
  const checks = (data.metrics.checks || {}).values || {};
  const failed = checks.fails || 0;
  const out = {
    stdout: `smoke: ${ITERATIONS} SKUs from fixture ${fixture.fixtureHash.slice(0, 16)}, `
      + `${checks.passes || 0} checks passed, ${failed} failed\n`,
  };

  if (SUMMARY_OUT !== '') {
    out[SUMMARY_OUT] = JSON.stringify(
      {
        schema: 'dpl.load-smoke.v1',
        baseUrl: BASE_URL,
        fixtureHash: fixture.fixtureHash,
        iterations: ITERATIONS,
        checksPassed: checks.passes || 0,
        checksFailed: failed,
      },
      null,
      2,
    );
  }

  return out;
}
