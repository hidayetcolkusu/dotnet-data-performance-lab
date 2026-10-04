# Cache off/on load comparison

Status: **invalid**

## Runs

| Repetition | Order | Arm | Requests | avg ms | med ms | p95 ms | p99 ms | Valid |
| ---: | :--- | :--- | ---: | ---: | ---: | ---: | ---: | :--- |
| 1 | AB | cache-off | 1201 | 6.94 | 5.48 | 16.26 | 26.23 | yes |
| 1 | AB | cache-on | 1200 | 6.08 | 5.01 | 13.49 | 26.68 | yes |
| 2 | BA | cache-off | 1201 | 6.30 | 4.53 | 14.90 | 26.34 | yes |
| 2 | BA | cache-on | 1200 | 7.83 | 6.28 | 16.67 | 28.09 | yes |
| 3 | AB | cache-off | 1201 | 4.56 | 3.50 | 11.50 | 19.60 | yes |
| 3 | AB | cache-on | 1201 | 6.75 | 5.50 | 15.51 | 24.07 | yes |
| 4 | BA | cache-off | 1201 | 3.69 | 3.01 | 7.02 | 13.55 | yes |
| 4 | BA | cache-on | - | - | - | - | - | no |
| 5 | AB | cache-off | 1201 | 3.57 | 2.95 | 7.46 | 12.00 | yes |
| 5 | AB | cache-on | 1201 | 5.78 | 4.60 | 12.19 | 27.97 | yes |

## Across repetitions

Percentiles are **not** pooled across runs. Each column below is a statistic *of the
per-run statistic*, over the valid runs of that arm.

| Arm | Valid runs | Requests | median of run p95 ms | min run p95 ms | max run p95 ms | median of run median ms |
| :--- | ---: | ---: | ---: | ---: | ---: | ---: |
| cache-off | 5/5 | 6005 | 11.50 | 7.02 | 16.26 | 3.50 |
| cache-on | 4/5 | 4802 | 13.49 | 12.19 | 16.67 | 5.01 |

## Why this comparison is invalid

- 'runs/4-BA-cache-on.summary.json': 313 dropped iterations; the target arrival rate was not sustained, so the latency is not comparable.
- the arms have different valid run counts; the comparison is unbalanced. cache-off=5, cache-on=4
