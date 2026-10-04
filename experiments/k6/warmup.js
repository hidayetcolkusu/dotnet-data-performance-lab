// Warmup, run in its own k6 process before every measured run so none of this traffic can
// land in the measured summary.
//
// Two phases, identical in both arms:
//   preload  one request per fixture SKU, so the measured run starts from the same coverage
//            on both sides (cache-on begins with the fixture resident; cache-off warms the
//            same SQL pages).
//   settle   the measured arrival rate for 30s, so connection pools, the JIT and the SQL
//            plan cache are in steady state when measurement begins.
//
// The measured run still lasts 60s against a 60s TTL, so entries do expire and are re-read
// during measurement. That is deliberate: hiding TTL renewal would overstate the cache.

import http from 'k6/http';
import { check } from 'k6';
import { scenario } from 'k6/execution';
import { baseUrl, config, fixture, pickProduct } from './lib/fixture.js';

const BASE_URL = baseUrl();
const RATE = config.rate();
const SETTLE_DURATION = config.warmupDuration();
const SKU_COUNT = fixture.products.length;

// A fixed budget, so the warmup costs the same wall clock in both arms rather than however
// long each happened to take. The threshold below turns an overrun into a failure instead
// of a silently half-preloaded cache.
const PRELOAD_BUDGET_SECONDS = 20;

export const options = {
  scenarios: {
    preload: {
      executor: 'shared-iterations',
      vus: 10,
      iterations: SKU_COUNT,
      maxDuration: `${PRELOAD_BUDGET_SECONDS}s`,
    },
    settle: {
      executor: 'constant-arrival-rate',
      rate: RATE,
      timeUnit: '1s',
      duration: SETTLE_DURATION,
      preAllocatedVUs: 20,
      maxVUs: 100,
      startTime: `${PRELOAD_BUDGET_SECONDS}s`,
    },
  },
  thresholds: {
    // A warmup that could not read the fixture means the measured run would be meaningless,
    // so correctness still gates it. Throughput deliberately does not.
    checks: ['rate==1'],
    http_req_failed: ['rate==0'],
    // Every fixture SKU must have been fetched before the measured run starts.
    'iterations{scenario:preload}': [`count>=${SKU_COUNT}`],
  },
};

export default function () {
  // The settle phase continues where preload stopped, so it does not re-walk the fixture
  // from the front and give the first SKUs an extra visit.
  const index = scenario.name === 'preload'
    ? scenario.iterationInTest
    : scenario.iterationInTest + SKU_COUNT;
  const expected = pickProduct(index);

  const response = http.get(`${BASE_URL}/api/products/${expected.sku}`, {
    tags: { name: 'warmup' },
    headers: { Accept: 'application/json' },
  });

  check(response, {
    'warmup status is 200': (r) => r.status === 200,
    'warmup sku matches': (r) => {
      try {
        return r.json('sku') === expected.sku;
      } catch (err) {
        return false;
      }
    },
  });
}

export function handleSummary() {
  return { stdout: `warmup complete: ${SKU_COUNT} SKUs preloaded, settled ${SETTLE_DURATION} at ${RATE}/s\n` };
}
