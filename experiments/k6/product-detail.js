// The measured arm of the cache off/on comparison.
//
// One iteration is exactly one GET /api/products/{sku}. The only difference between the two
// arms is the API's Cache:Enabled setting; this script, the fixture and the load profile are
// identical on both sides. Nothing here writes to the database or to the cache.

import http from 'k6/http';
import { check } from 'k6';
import { Trend } from 'k6/metrics';
import { scenario } from 'k6/execution';
import {
  arm, baseUrl, config, fixture, pickProduct, priceMatches, buildSummary,
} from './lib/fixture.js';

const RATE = config.rate();
const DURATION = config.duration();
const SUMMARY_OUT = config.summaryOut();
const BASE_URL = baseUrl();

// Latency of successful, correct responses only. http_req_duration would blend in the
// failures, and a fast 404 would flatter the arm that served it.
const detailLatency = new Trend('detail_latency_ms', true);

export const options = {
  scenarios: {
    measured: {
      executor: 'constant-arrival-rate',
      rate: RATE,
      timeUnit: '1s',
      duration: DURATION,
      preAllocatedVUs: 20,
      maxVUs: 100,
    },
  },
  thresholds: {
    checks: ['rate==1'],
    http_req_failed: ['rate==0'],
    // If k6 could not start an iteration on time the requested load was never applied,
    // so the run is not a measurement of the requested rate.
    dropped_iterations: ['count==0'],
  },
  summaryTrendStats: ['avg', 'med', 'p(95)', 'p(99)', 'min', 'max', 'count'],
  // The comparison starts the API fresh per arm; a connection reused from a previous run
  // would give one arm a warm socket the other did not have.
  noConnectionReuse: false,
  discardResponseBodies: false,
};

export function setup() {
  return { arm: arm(), fixtureHash: fixture.fixtureHash, skus: fixture.products.length };
}

export default function () {
  const expected = pickProduct(scenario.iterationInTest);
  const response = http.get(`${BASE_URL}/api/products/${expected.sku}`, {
    tags: { name: 'product-detail' },
    headers: { Accept: 'application/json' },
  });

  let body = null;
  if (response.status === 200) {
    try {
      body = response.json();
    } catch (err) {
      // A 200 we cannot parse is a failure, not a fast response.
      body = null;
    }
  }

  const ok = check(response, {
    'status is 200': (r) => r.status === 200,
    'body parses': () => body !== null,
    'sku matches the fixture': () => body !== null && body.sku === expected.sku,
    'id matches the fixture': () => body !== null && body.id === expected.id,
    'name matches the fixture': () => body !== null && body.name === expected.name,
    'categoryId matches the fixture': () => body !== null && body.categoryId === expected.categoryId,
    'unitPrice matches the fixture': () => body !== null && priceMatches(body.unitPrice, expected.unitPrice),
    'isActive matches the fixture': () => body !== null && body.isActive === expected.isActive,
    'detail fields are present': () => body !== null
      && typeof body.description === 'string'
      && typeof body.createdAtUtc === 'string'
      && typeof body.version === 'string' && body.version.length > 0,
  });

  if (ok) {
    detailLatency.add(response.timings.duration);
  }
}

export function handleSummary(data) {
  const summary = buildSummary(data, {});
  const out = {
    stdout: `${summary.arm} rep ${summary.repetition} (${summary.order}): `
      + `${summary.metrics.httpReqs} requests, `
      + `med ${summary.metrics.detailLatencyMs.medMs.toFixed(2)} ms, `
      + `p95 ${summary.metrics.detailLatencyMs.p95Ms.toFixed(2)} ms, `
      + `${summary.metrics.checksFailed} failed checks, `
      + `${summary.metrics.droppedIterations} dropped iterations\n`,
  };

  if (SUMMARY_OUT !== '') {
    out[SUMMARY_OUT] = JSON.stringify(summary, null, 2);
  }

  return out;
}
